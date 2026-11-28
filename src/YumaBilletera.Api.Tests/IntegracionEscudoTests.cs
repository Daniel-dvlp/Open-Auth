using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YumaBilletera.Api.Shared.Escudo;
using YumaBilletera.Api.Shared.Persistencia;

namespace YumaBilletera.Api.Tests;

/// <summary>Fase 3: bandeja hacia el Escudo, reevaluación con falla cerrada, avisos de resolución firmados y conciliación.</summary>
public class IntegracionEscudoTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<(FabricaBilletera F, HttpClient C)> PrepararAsync()
    {
        var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010", 1_000_000);
        await f.SembrarClienteAsync("C00011", 0);
        return (f, await f.ClienteAutenticadoAsync("C00010"));
    }

    private static async Task<string> RetenerAsync(FabricaBilletera f, HttpClient c, long monto = 400_000, string clave = "k1")
    {
        f.Escudo.Decidir = _ => FakeEscudo.D("retener");
        var j = await Json(await c.SendAsync(Http.Post("/v1/transferencias", new { llave = "3000000011", monto_cop = monto }, clave)));
        Assert.Equal("en_revision", j.GetProperty("estado").GetString());
        return j.GetProperty("id_transaccion").GetString()!;
    }

    private static (long Saldo, long Reservado, string Estado) Cuenta(FabricaBilletera f, string id) =>
        f.ConBd(db => db.Cuentas.Where(c => c.IdCuenta == id).AsEnumerable().Select(c => (c.SaldoCop, c.ReservadoCop, c.Estado)).Single());

    private static HttpRequestMessage Aviso(string cuerpo, long? marca = null, string? secreto = null)
    {
        var ts = marca ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var firma = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secreto ?? FabricaBilletera.SecretoWebhook),
            Encoding.UTF8.GetBytes($"{ts}.{cuerpo}"))).ToLowerInvariant();
        var m = new HttpRequestMessage(HttpMethod.Post, "/v1/internas/resoluciones") { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };
        m.Headers.Add("X-Yuma-Marca-Tiempo", ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        m.Headers.Add("X-Yuma-Firma", firma);
        return m;
    }

    private static string CuerpoAviso(string id, string resolucion, string accion, bool bloqueo = false) => JsonSerializer.Serialize(new
    {
        id_transaccion = id, id_cliente = "C00010", resolucion, accion, bloqueo_definitivo = bloqueo, resuelta_utc = DateTime.UtcNow,
    });

    [Fact]
    public async Task Falso_positivo_del_analista_libera_la_operacion_retenida()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        var id = await RetenerAsync(f, c);
        var r = await f.CreateClient().SendAsync(Aviso(CuerpoAviso(id, "falso_positivo", "liberar_transaccion")));
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal((600_000L, 0L, "activa"), Cuenta(f, "C00010"));
        Assert.Equal(400_000L, Cuenta(f, "C00011").Saldo);
        Assert.Equal("completada", (await c.GetFromJsonAsync<JsonElement>($"/v1/transferencias/{id}")).GetProperty("estado").GetString());
        // Aviso repetido: idempotente, no vuelve a mover dinero.
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().SendAsync(Aviso(CuerpoAviso(id, "falso_positivo", "liberar_transaccion")))).StatusCode);
        Assert.Equal(400_000L, Cuenta(f, "C00011").Saldo);
    }

    [Fact]
    public async Task Fraude_confirmado_con_bloqueo_el_dinero_no_sale_y_la_cuenta_queda_bloqueada()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        var id = await RetenerAsync(f, c);
        Assert.Equal(HttpStatusCode.NoContent,
            (await f.CreateClient().SendAsync(Aviso(CuerpoAviso(id, "fraude_confirmado", "bloquear_cuenta", bloqueo: true)))).StatusCode);
        Assert.Equal((1_000_000L, 0L, "bloqueada"), Cuenta(f, "C00010"));
        Assert.Equal(0L, Cuenta(f, "C00011").Saldo);
        // Cuenta bloqueada: no puede volver a enviar.
        f.Escudo.Decidir = _ => FakeEscudo.D("aprobar");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.SendAsync(Http.Post("/v1/transferencias", new { llave = "3000000011", monto_cop = 1_000 }, "k2"))).StatusCode);
    }

    [Fact]
    public async Task Mantener_retencion_deja_el_monto_retenido()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        var id = await RetenerAsync(f, c);
        await f.CreateClient().SendAsync(Aviso(CuerpoAviso(id, "fraude_confirmado", "mantener_retencion")));
        Assert.Equal((1_000_000L, 400_000L, "activa"), Cuenta(f, "C00010"));
    }

    [Fact]
    public async Task Aviso_con_firma_invalida_o_vencida_se_rechaza()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        var id = await RetenerAsync(f, c);
        var cuerpo = CuerpoAviso(id, "falso_positivo", "liberar_transaccion");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().SendAsync(Aviso(cuerpo, secreto: "otro-secreto-de-al-menos-32-caracteres-xx"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().SendAsync(Aviso(cuerpo, marca: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()))).StatusCode);
        var alterado = Aviso(cuerpo);
        alterado.Content = new StringContent(cuerpo.Replace("falso_positivo", "fraude_confirmado"), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().SendAsync(alterado)).StatusCode);
        Assert.Equal((1_000_000L, 400_000L, "activa"), Cuenta(f, "C00010"));
    }

    [Fact]
    public async Task Escudo_caido_y_luego_disponible_la_operacion_se_reevalua()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        f.Escudo.Caido = true;
        var id = (await Json(await c.SendAsync(Http.Post("/v1/transferencias", new { llave = "3000000011", monto_cop = 200_000 }, "k1"))))
            .GetProperty("id_transaccion").GetString();
        await f.CicloIntegracionAsync(); // sigue caído: no cambia nada
        Assert.Equal((1_000_000L, 200_000L, "activa"), Cuenta(f, "C00010"));

        f.Escudo.Caido = false;
        f.Escudo.Decidir = _ => FakeEscudo.D("aprobar");
        await f.ConBdAsync(async (db, _) => // "hace un rato": la reevaluación espera unos segundos tras la falla
        {
            var t = await db.Transferencias.SingleAsync(x => x.IdTransaccion == id);
            t.CreadaUtc = t.CreadaUtc.AddMinutes(-1);
            await db.SaveChangesAsync();
        });
        await f.CicloIntegracionAsync();
        Assert.Equal((800_000L, 0L, "activa"), Cuenta(f, "C00010"));
        Assert.Equal("completada", (await c.GetFromJsonAsync<JsonElement>($"/v1/transferencias/{id}")).GetProperty("estado").GetString());
    }

    [Fact]
    public async Task Conciliacion_aplica_una_resolucion_cuyo_aviso_se_perdio()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        var id = await RetenerAsync(f, c);
        f.Escudo.Caso = x => x == id ? new EstadoCasoEscudo(id, "resuelta", "retener", "falso_positivo", "liberar_transaccion", false, DateTime.UtcNow) : null;
        await f.CicloIntegracionAsync();
        Assert.Equal(400_000L, Cuenta(f, "C00011").Saldo);
    }

    [Fact]
    public async Task Bandeja_entrega_eventos_con_esquema_del_caso_y_el_desafio_fallido()
    {
        var (f, c) = await PrepararAsync();
        using var fabrica = f;
        f.Escudo.Decidir = _ => FakeEscudo.D("desafiar", "verificar");
        var j = await Json(await c.SendAsync(Http.Post("/v1/transferencias", new { llave = "3000000011", monto_cop = 300_000 }, "k1")));
        await c.PostAsJsonAsync($"/v1/transferencias/{j.GetProperty("id_transaccion").GetString()}/biometria",
            new { id_verificacion = j.GetProperty("verificacion").GetProperty("id_verificacion").GetGuid(), resultado_simulado = "no_supera" });
        await c.PostAsJsonAsync("/v1/dispositivos", new { id_dispositivo = "DISP-2", sistema_operativo = "iOS" });
        await f.CicloIntegracionAsync();

        Assert.Contains(f.Escudo.Eventos, e => e is { TipoEvento: "inicio_sesion", Resultado: "exitoso", IdCliente: "C00010" });
        Assert.Contains(f.Escudo.Eventos, e => e is { TipoEvento: "registro_dispositivo", IdDispositivo: "DISP-2", SistemaOperativo: "iOS" });
        Assert.All(f.Escudo.Eventos, e => Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", e.FechaHora));
        var desafio = Assert.Single(f.Escudo.DesafiosFallidos);
        Assert.Equal("biometria", desafio.GetProperty("metodo").GetString());
        Assert.Equal(300_000, desafio.GetProperty("monto_cop").GetInt64());
        Assert.Equal(0, f.ConBd(db => db.Bandeja.Count(b => b.EnviadoUtc == null)));
    }

    [Fact]
    public async Task Un_ingreso_fallido_llega_al_Escudo_antes_del_score_del_siguiente_ingreso()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010");
        await f.LoginAsync("C00010", clave: "mala");
        Assert.Empty(f.Escudo.Eventos);
        await f.LoginAsync("C00010");
        Assert.Contains(f.Escudo.Eventos, e => e.Resultado == "fallido"); // se entregó antes de pedir el score de ingreso
    }
}
