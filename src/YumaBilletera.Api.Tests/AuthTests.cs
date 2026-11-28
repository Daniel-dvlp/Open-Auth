using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using YumaBilletera.Api.Shared.Escudo;
using YumaBilletera.Api.Shared.Persistencia;

namespace YumaBilletera.Api.Tests;

public class AuthTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Ingreso_permitido_emite_tokens_y_avisa_al_Escudo()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010");
        var r = await f.LoginAsync("C00010");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("permitir", j.GetProperty("resultado").GetString());
        Assert.False(string.IsNullOrEmpty(j.GetProperty("token").GetString()));
        Assert.False(string.IsNullOrEmpty(j.GetProperty("refresh_token").GetString()));
        var ingreso = Assert.Single(f.Escudo.Ingresos);
        Assert.Equal("C00010", ingreso.IdCliente);
        Assert.Contains("DISP-1", ingreso.DispositivosConocidos);
        // El ingreso exitoso queda en la bandeja hacia el Escudo con el esquema de eventos_app.csv.
        var ev = f.ConBd(db => db.Bandeja.Select(b => b.CargaJson).ToList()).Select(c => JsonSerializer.Deserialize<EventoApp>(c)!).Single();
        Assert.Equal(("inicio_sesion", "exitoso", "C00010"), (ev.TipoEvento, ev.Resultado, ev.IdCliente));
    }

    [Fact]
    public async Task Clave_incorrecta_responde_401_generico_y_el_fallido_llega_al_Escudo()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010");
        var r = await f.LoginAsync("C00010", clave: "otra-clave");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var noExiste = await f.LoginAsync("C09999", clave: "otra-clave");
        Assert.Equal(HttpStatusCode.Unauthorized, noExiste.StatusCode); // mismo código: no revela si la llave existe
        var ev = JsonSerializer.Deserialize<EventoApp>(f.ConBd(db => db.Bandeja.Single().CargaJson))!;
        Assert.Equal(("inicio_sesion", "fallido"), (ev.TipoEvento, ev.Resultado));
        Assert.Empty(f.Escudo.Ingresos); // sin clave válida no se consulta el score de ingreso
    }

    [Fact]
    public async Task Escudo_pide_otp_y_el_ingreso_se_completa_con_el_codigo()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010");
        f.Escudo.Ingreso = _ => new ResultadoIngresoEscudo("otp", 0.45, ["Dispositivo no visto antes para este cliente"]);
        var j = await Json(await f.LoginAsync("C00010", dispositivo: "DISP-NUEVO"));
        Assert.Equal("verificacion_requerida", j.GetProperty("resultado").GetString());
        Assert.True(j.GetProperty("token").ValueKind == JsonValueKind.Null);
        var v = j.GetProperty("verificacion");
        Assert.Equal("otp", v.GetProperty("metodo").GetString());
        var id = v.GetProperty("id_verificacion").GetGuid();

        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/v1/auth/otp", new { id_verificacion = id, codigo = "000000" == Leer() ? "111111" : "000000" })).StatusCode);
        var ok = await c.PostAsJsonAsync("/v1/auth/otp", new { id_verificacion = id, codigo = Leer() });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(string.IsNullOrEmpty((await Json(ok)).GetProperty("token").GetString()));
        // Reusar la verificación ya resuelta no abre otra sesión.
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/v1/auth/otp", new { id_verificacion = id, codigo = Leer() })).StatusCode);

        string Leer() => f.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/dev/otp/{id}").Result.GetProperty("codigo").GetString()!;
    }

    [Fact]
    public async Task Otp_se_agota_al_tercer_intento()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010");
        f.Escudo.Ingreso = _ => new ResultadoIngresoEscudo("otp", 0.45, ["x"]);
        var id = (await Json(await f.LoginAsync("C00010"))).GetProperty("verificacion").GetProperty("id_verificacion").GetGuid();
        var real = (await f.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/dev/otp/{id}")).GetProperty("codigo").GetString()!;
        var malo = real == "123456" ? "654321" : "123456";
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/v1/auth/otp", new { id_verificacion = id, codigo = malo })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/v1/auth/otp", new { id_verificacion = id, codigo = malo })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/v1/auth/otp", new { id_verificacion = id, codigo = malo })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await c.PostAsJsonAsync("/v1/auth/otp", new { id_verificacion = id, codigo = real })).StatusCode);
    }

    [Fact]
    public async Task Con_cambio_de_SIM_reciente_no_se_acepta_otp_y_se_exige_biometria()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010");
        f.Escudo.Ingreso = _ => new ResultadoIngresoEscudo("otp", 0.55, ["Cambio de SIM en las últimas 48 horas"]);
        Assert.Equal(HttpStatusCode.Accepted, (await f.CreateClient().PostAsJsonAsync("/v1/dev/cambio-sim", new { id_cliente = "C00010" })).StatusCode);
        var v = (await Json(await f.LoginAsync("C00010"))).GetProperty("verificacion");
        Assert.Equal("biometria", v.GetProperty("metodo").GetString());
        var id = v.GetProperty("id_verificacion").GetGuid();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/v1/auth/otp", new { id_verificacion = id, codigo = "123456" })).StatusCode);
        var ok = await c.PostAsJsonAsync("/v1/auth/biometria", new { id_verificacion = id, resultado_simulado = "supera" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Biometria_no_superada_cierra_el_ingreso()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010");
        f.Escudo.Ingreso = _ => new ResultadoIngresoEscudo("otp", 0.55, ["x"]);
        await f.CreateClient().PostAsJsonAsync("/v1/dev/cambio-sim", new { id_cliente = "C00010" });
        var id = (await Json(await f.LoginAsync("C00010"))).GetProperty("verificacion").GetProperty("id_verificacion").GetGuid();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/v1/auth/biometria", new { id_verificacion = id, resultado_simulado = "no_supera" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await c.PostAsJsonAsync("/v1/auth/biometria", new { id_verificacion = id, resultado_simulado = "supera" })).StatusCode);
    }

    [Fact]
    public async Task Escudo_caido_en_el_ingreso_exige_verificacion_falla_cerrada()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010");
        f.Escudo.Caido = true;
        var j = await Json(await f.LoginAsync("C00010"));
        Assert.Equal("verificacion_requerida", j.GetProperty("resultado").GetString());
    }

    [Fact]
    public async Task Bloqueo_temporal_responde_403_sin_token()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010");
        f.Escudo.Ingreso = _ => new ResultadoIngresoEscudo("bloqueo_temporal", 0.8, ["x"]);
        var r = await f.LoginAsync("C00010");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("bloqueo_temporal", (await Json(r)).GetProperty("resultado").GetString());
    }

    [Fact]
    public async Task Refresh_es_rotativo_y_de_un_solo_uso()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010");
        var refresh = (await Json(await f.LoginAsync("C00010"))).GetProperty("refresh_token").GetString();
        var c = f.CreateClient();
        var r1 = await c.PostAsJsonAsync("/v1/auth/refresh", new { refresh_token = refresh });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.NotEqual(refresh, (await Json(r1)).GetProperty("refresh_token").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/v1/auth/refresh", new { refresh_token = refresh })).StatusCode);
    }

    [Fact]
    public async Task Cambio_de_clave_cierra_las_otras_sesiones_y_queda_como_senal()
    {
        using var f = new FabricaBilletera();
        await f.SembrarClienteAsync("C00010");
        var otra = await f.ClienteAutenticadoAsync("C00010");
        var actual = await f.ClienteAutenticadoAsync("C00010");
        Assert.Equal(HttpStatusCode.NoContent,
            (await actual.PostAsJsonAsync("/v1/seguridad/clave", new { clave_actual = FabricaBilletera.Clave, clave_nueva = "Nueva-Clave-2026" })).StatusCode);
        Assert.Equal(1, f.ConBd(db => db.Sesiones.Count(s => s.Estado == EstadosSesion.Activa)));
        Assert.Equal(HttpStatusCode.OK, (await f.LoginAsync("C00010", clave: "Nueva-Clave-2026")).StatusCode);

        // La sesión cerrada ya no puede mover dinero, y la siguiente operación lleva cambio_clave_24h = 1.
        var req = new { llave = "3000000011", monto_cop = 1_000 };
        await f.SembrarClienteAsync("C00011");
        var r = await otra.PostAsJsonAsync("/v1/transferencias", req, new Dictionary<string, string> { ["Idempotency-Key"] = "k1" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        await actual.SendAsync(Http.Post("/v1/transferencias", req, "k2"));
        Assert.Equal(1, Assert.Single(f.Escudo.Recibidas).CambioClave24h);
    }
}

internal static class Http
{
    public static HttpRequestMessage Post(string url, object cuerpo, string? idempotencia)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(cuerpo) };
        if (idempotencia is not null) m.Headers.Add("Idempotency-Key", idempotencia);
        return m;
    }

    public static Task<HttpResponseMessage> PostAsJsonAsync(this HttpClient c, string url, object cuerpo, Dictionary<string, string> cabeceras)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(cuerpo) };
        foreach (var kv in cabeceras) m.Headers.Add(kv.Key, kv.Value);
        return c.SendAsync(m);
    }
}
