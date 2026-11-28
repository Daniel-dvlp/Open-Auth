using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using YumaBilletera.Api.Shared.Contabilidad;
using YumaBilletera.Api.Shared.Persistencia;

namespace YumaBilletera.Api.Tests;

/// <summary>Flujo de autorización del caso: el Escudo decide ANTES de que el dinero salga.</summary>
public class OperacionesTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<(FabricaBilletera F, HttpClient C)> PrepararAsync(long saldo = 1_000_000)
    {
        var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010", saldo);
        await f.SembrarClienteAsync("C00011", 0, depto: "Bogotá D.C.");
        return (f, await f.ClienteAutenticadoAsync("C00010"));
    }

    private static Task<HttpResponseMessage> Transferir(HttpClient c, long monto, string clave = "k1", string llave = "3000000011") =>
        c.SendAsync(Http.Post("/v1/transferencias", new { llave, monto_cop = monto }, clave));

    private static (long Saldo, long Reservado) Cuenta(FabricaBilletera f, string id) =>
        f.ConBd(db => db.Cuentas.Where(c => c.IdCuenta == id).Select(c => new { c.SaldoCop, c.ReservadoCop }).AsEnumerable()
            .Select(c => (c.SaldoCop, c.ReservadoCop)).Single());

    [Fact]
    public async Task Aprobada_mueve_el_dinero_con_doble_partida()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        var r = await Transferir(c, 250_000);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("completada", j.GetProperty("estado").GetString());
        Assert.Equal((750_000L, 0L), Cuenta(f, "C00010"));
        Assert.Equal((250_000L, 0L), Cuenta(f, "C00011"));
        Assert.Equal(0, f.ConBd(db => db.Cuentas.Sum(x => x.SaldoCop)));
        // No se exponen puntaje ni razones del Escudo al cliente (no se publican reglas ni umbrales).
        Assert.False(j.TryGetProperty("puntaje", out _));
        Assert.False(j.TryGetProperty("razones", out _));
        var movs = await c.GetFromJsonAsync<JsonElement>("/v1/cuentas/me/movimientos");
        var ultimo = movs.GetProperty("movimientos")[0];
        Assert.Equal(("debito", 250_000L, 750_000L), (ultimo.GetProperty("tipo").GetString(), ultimo.GetProperty("monto_cop").GetInt64(), ultimo.GetProperty("saldo_despues_cop").GetInt64()));
    }

    [Fact]
    public async Task Senales_del_esquema_Transaccion_salen_de_la_billetera()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010", vinculacion: new DateOnly(2026, 1, 1));
        await f.SembrarClienteAsync("C00011", 0);
        var c = await f.ClienteAutenticadoAsync("C00010", depto: "Bogotá D.C.", dispositivo: "DISP-1");
        await Transferir(c, 10_000, "a");
        await Transferir(c, 10_000, "b");
        var txs = f.Escudo.Recibidas.ToArray();
        var t1 = txs[0];
        Assert.Equal(("transferencia_inmediata", "app", "cuenta_yuma", "C00011"), (t1.TipoOperacion, t1.Canal, t1.TipoDestino, t1.IdDestino));
        Assert.Equal(1, t1.DestinoNuevo);             // nunca le había enviado
        Assert.Equal(0, txs[1].DestinoNuevo);         // la segunda ya no es destino nuevo
        Assert.Equal(1, t1.IpDepartamentoDistinto);   // habitual Antioquia, IP en Bogotá D.C.
        Assert.Equal(0, t1.DispositivoNuevo);         // registrado hace 30 días
        Assert.Equal(1_000_000, t1.SaldoAntesCop);
        Assert.Equal(990_000, txs[1].SaldoAntesCop);
        Assert.Equal(DateOnly.FromDateTime(HoraColombia.DesdeUtc(DateTime.UtcNow)).DayNumber - new DateOnly(2026, 1, 1).DayNumber, t1.AntiguedadCuentaDias);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", t1.FechaHora); // hora de Colombia, formato del caso
        Assert.True(t1.IdTransaccion.Length <= 32);

        await f.CreateClient().PostAsJsonAsync("/v1/dev/cambio-sim", new { id_cliente = "C00010" });
        await Transferir(c, 10_000, "c");
        Assert.Equal(1, f.Escudo.Recibidas.Last().CambioSim48h);
    }

    [Fact]
    public async Task Idempotencia_un_reintento_no_duplica_el_envio()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        var r1 = await Json(await Transferir(c, 100_000, "misma"));
        var r2 = await Transferir(c, 100_000, "misma");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(r1.GetProperty("id_transaccion").GetString(), (await Json(r2)).GetProperty("id_transaccion").GetString());
        Assert.Equal(900_000, Cuenta(f, "C00010").Saldo);
        Assert.Single(f.Escudo.Recibidas);
        Assert.Equal(HttpStatusCode.Conflict, (await Transferir(c, 5_000, "misma")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Http.Post("/v1/transferencias", new { llave = "3000000011", monto_cop = 1 }, null))).StatusCode);
    }

    [Fact]
    public async Task Desafio_exige_biometria_y_al_superarla_se_completa()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        f.Escudo.Decidir = _ => FakeEscudo.D("desafiar", "verificar");
        var j = await Json(await Transferir(c, 300_000));
        Assert.Equal("requiere_verificacion", j.GetProperty("estado").GetString());
        Assert.Equal("biometria", j.GetProperty("verificacion").GetProperty("metodo").GetString());
        Assert.Equal((1_000_000L, 300_000L), Cuenta(f, "C00010")); // reservado, no ha salido
        var id = j.GetProperty("id_transaccion").GetString();
        var r = await c.PostAsJsonAsync($"/v1/transferencias/{id}/biometria",
            new { id_verificacion = j.GetProperty("verificacion").GetProperty("id_verificacion").GetGuid(), resultado_simulado = "supera" });
        Assert.Equal("completada", (await Json(r)).GetProperty("estado").GetString());
        Assert.Equal((700_000L, 0L), Cuenta(f, "C00010"));
    }

    [Fact]
    public async Task Biometria_no_superada_pasa_a_analista_y_el_dinero_no_sale()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        f.Escudo.Decidir = _ => FakeEscudo.D("desafiar", "verificar");
        var j = await Json(await Transferir(c, 300_000));
        var id = j.GetProperty("id_transaccion").GetString();
        var r = await Json(await c.PostAsJsonAsync($"/v1/transferencias/{id}/biometria",
            new { id_verificacion = j.GetProperty("verificacion").GetProperty("id_verificacion").GetGuid(), resultado_simulado = "no_supera" }));
        Assert.Equal("en_revision", r.GetProperty("estado").GetString());
        Assert.Equal((1_000_000L, 300_000L), Cuenta(f, "C00010"));
        Assert.Contains(f.ConBd(db => db.Bandeja.Select(b => b.Tipo).ToList()), t => t == "desafio_fallido");
    }

    [Fact]
    public async Task Advertencia_anti_ingenieria_social_en_el_mensaje()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        f.Escudo.Decidir = _ => FakeEscudo.D("desafiar", "advertir");
        var j = await Json(await Transferir(c, 900_000));
        Assert.Contains("cuenta segura", j.GetProperty("mensaje").GetString());
    }

    [Theory]
    [InlineData("retener", "en_revision", 1_000_000, 400_000)]
    [InlineData("rechazar", "rechazada", 1_000_000, 0)]
    public async Task Retener_y_rechazar_no_mueven_el_dinero(string decision, string estado, long saldo, long reservado)
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        f.Escudo.Decidir = _ => FakeEscudo.D(decision);
        Assert.Equal(estado, (await Json(await Transferir(c, 400_000))).GetProperty("estado").GetString());
        Assert.Equal((saldo, reservado), Cuenta(f, "C00010"));
        Assert.Equal(0L, Cuenta(f, "C00011").Saldo);
    }

    [Fact]
    public async Task Escudo_caido_falla_cerrado_la_operacion_queda_en_revision()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        f.Escudo.Caido = true;
        var j = await Json(await Transferir(c, 200_000));
        Assert.Equal("en_revision", j.GetProperty("estado").GetString());
        Assert.Equal((1_000_000L, 200_000L), Cuenta(f, "C00010"));
        Assert.Equal(0L, Cuenta(f, "C00011").Saldo);
    }

    [Fact]
    public async Task Saldo_insuficiente_y_cuenta_propia_se_rechazan_sin_consultar_al_Escudo()
    {
        var (f, c) = await PrepararAsync(50_000);
        using var fabrica = f;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Transferir(c, 50_001)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Transferir(c, 1_000, "propia", "3000000010")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Transferir(c, 1_000, "nadie", "3999999999")).StatusCode);
        Assert.Empty(f.Escudo.Recibidas);
    }

    [Fact]
    public async Task Pagos_y_recargas_usan_los_tipos_del_caso()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        Assert.Equal(HttpStatusCode.Created, (await c.SendAsync(Http.Post("/v1/pagos/qr", new { codigo_comercio = "COM0412", monto_cop = 35_000 }, "q"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.SendAsync(Http.Post("/v1/pagos/servicios", new { id_servicio = "SRV07", referencia_pago = "4455667788", monto_cop = 82_500 }, "s"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.SendAsync(Http.Post("/v1/recargas", new { id_operador = "OPE03", celular = "310 555 1234", monto_cop = 10_000 }, "r"))).StatusCode);
        Assert.Equal(
            new[] { ("pago_qr", "comercio", "COM0412"), ("pago_servicios", "servicio", "SRV07"), ("recarga_celular", "operador", "OPE03") },
            f.Escudo.Recibidas.Select(t => (t.TipoOperacion, t.TipoDestino, t.IdDestino)).ToArray());
        Assert.Equal(1_000_000 - 35_000 - 82_500 - 10_000, Cuenta(f, "C00010").Saldo);
        Assert.Equal(35_000, Cuenta(f, CuentasInternas.Puente("comercio")).Saldo);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Http.Post("/v1/pagos/qr", new { codigo_comercio = "XYZ", monto_cop = 1 }, "q2"))).StatusCode);
    }

    [Fact]
    public async Task Destinatario_de_otra_entidad_no_expone_el_numero_de_producto()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        var r = await c.PostAsJsonAsync("/v1/destinatarios", new { alias = "Arriendo", tipo_destino = "otra_entidad", entidad = "Banco de prueba", numero_producto = "0012345678" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var d = await Json(r);
        Assert.Equal("****5678", d.GetProperty("producto").GetString());
        Assert.StartsWith("EXT", d.GetProperty("id_destino").GetString());
        var t = await Json(await c.SendAsync(Http.Post("/v1/transferencias", new { id_destinatario = d.GetProperty("id").GetGuid(), monto_cop = 120_000 }, "ext")));
        Assert.Equal("completada", t.GetProperty("estado").GetString());
        Assert.Equal("otra_entidad", f.Escudo.Recibidas.Single().TipoDestino);
        Assert.Equal(120_000, Cuenta(f, CuentasInternas.Puente("otra_entidad")).Saldo);
    }

    [Fact]
    public async Task Un_cliente_no_ve_operaciones_ajenas()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        var id = (await Json(await Transferir(c, 1_000))).GetProperty("id_transaccion").GetString();
        var otro = await f.ClienteAutenticadoAsync("C00011");
        Assert.Equal(HttpStatusCode.NotFound, (await otro.GetAsync($"/v1/transferencias/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/v1/transferencias/{id}")).StatusCode);
    }

    [Fact]
    public async Task Limite_diario_configurable()
    {
        using var f = new FabricaBilletera(new Dictionary<string, string?> { ["Billetera:Limites:LimiteDiarioCop"] = "100000" });
        await f.SembrarClienteAsync("C00010");
        await f.SembrarClienteAsync("C00011", 0);
        var c = await f.ClienteAutenticadoAsync("C00010");
        Assert.Equal(HttpStatusCode.Created, (await Transferir(c, 80_000, "a")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Transferir(c, 20_001, "b")).StatusCode);
    }

    [Fact]
    public async Task Saldo_y_paginacion_de_movimientos()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        for (var i = 0; i < 3; i++) await Transferir(c, 1_000, "p" + i);
        var saldo = await c.GetFromJsonAsync<JsonElement>("/v1/cuentas/me");
        Assert.Equal((997_000L, "COP", "3000000010"), (saldo.GetProperty("disponible_cop").GetInt64(), saldo.GetProperty("moneda").GetString(), saldo.GetProperty("llave").GetString()));
        var p1 = await c.GetFromJsonAsync<JsonElement>("/v1/cuentas/me/movimientos?limite=2");
        Assert.Equal(2, p1.GetProperty("movimientos").GetArrayLength());
        var cursor = p1.GetProperty("siguiente_cursor").GetInt64();
        var p2 = await c.GetFromJsonAsync<JsonElement>($"/v1/cuentas/me/movimientos?limite=2&cursor={cursor}");
        Assert.Equal(2, p2.GetProperty("movimientos").GetArrayLength()); // 1 débito restante + apertura
        Assert.True(p2.GetProperty("siguiente_cursor").ValueKind == JsonValueKind.Null);
    }
}
