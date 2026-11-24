using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Persistencia;
using Microsoft.Extensions.DependencyInjection;

namespace EscudoYuma.Api.Tests;

public class LoginTests(FabricaApi api) : IClassFixture<FabricaApi>
{
    [Fact]
    public async Task Login_correcto_permite_y_el_token_contiene_el_score_y_la_sesion()
    {
        var llave = await api.SembrarAsync("C-LOGIN-OK");
        var resp = await api.LoginAsync(llave);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("permitir", json.GetProperty("resultado").GetString());
        Assert.True(json.GetProperty("score_ingreso").GetDouble() < 0.40);
        Assert.NotEmpty(json.GetProperty("razones").EnumerateArray());

        var jwt = FabricaApi.Leer(json.GetProperty("token").GetString()!);
        Assert.Equal("C-LOGIN-OK", jwt.Subject);
        Assert.False(string.IsNullOrEmpty(jwt.Claims.First(c => c.Type == "sid").Value));
        Assert.Equal(json.GetProperty("score_ingreso").GetDouble(),
            double.Parse(jwt.Claims.First(c => c.Type == "score_ingreso").Value, System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.True(jwt.ValidTo - jwt.ValidFrom <= TimeSpan.FromMinutes(15.1)); // vigencia corta
    }

    [Fact]
    public async Task Login_incorrecto_no_revela_si_la_llave_existe()
    {
        var llave = await api.SembrarAsync("C-LOGIN-MAL");
        var claveMala = await api.LoginAsync(llave, "clave-equivocada");
        var llaveInexistente = await api.LoginAsync("no-existe-esta-llave", "clave-equivocada");

        Assert.Equal(HttpStatusCode.Unauthorized, claveMala.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, llaveInexistente.StatusCode);
        // Cuerpos idénticos (salvo id de correlación/traza): mismo título y detalle.
        var a = await claveMala.Content.ReadFromJsonAsync<JsonElement>();
        var b = await llaveInexistente.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
        Assert.Equal(a.GetProperty("detail").GetString(), b.GetProperty("detail").GetString());
        Assert.DoesNotContain("existe", a.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dispositivo_desconocido_con_fallidos_recientes_exige_otp_y_no_entrega_token()
    {
        // score_login.py: dispositivo no visto = 0,30 (solo no alcanza el umbral de OTP 0,40); 2 fallidos en 1 h suman 0,24 => 0,54.
        var llave = await api.SembrarAsync("C-LOGIN-DISP");
        for (var i = 0; i < 2; i++) await api.LoginAsync(llave, "mala");
        var json = await (await api.LoginAsync(llave, dispositivo: "DISP-NUEVO")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("otp", json.GetProperty("resultado").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("token").ValueKind);
        Assert.InRange(json.GetProperty("score_ingreso").GetDouble(), 0.54, 0.62); // +0,08 si es de madrugada
        Assert.Contains(json.GetProperty("razones").EnumerateArray(), r => r.GetString()!.Contains("Dispositivo no visto"));
    }

    [Fact]
    public async Task Dispositivo_desconocido_solo_puntua_030_y_se_permite_segun_los_pesos_de_score_login()
    {
        var llave = await api.SembrarAsync("C-LOGIN-DISP-SOLO");
        var json = await (await api.LoginAsync(llave, dispositivo: "DISP-NUEVO")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(json.GetProperty("score_ingreso").GetDouble(), 0.30, 0.38); // 0,30 (+0,08 si es de madrugada)
        Assert.Equal("permitir", json.GetProperty("resultado").GetString());
    }

    [Fact]
    public async Task Un_login_con_otp_deja_su_score_en_el_estado_como_escalador_de_la_politica()
    {
        var llave = await api.SembrarAsync("C-LOGIN-ESCALA");
        for (var i = 0; i < 2; i++) await api.LoginAsync(llave, "mala");
        var json = await (await api.LoginAsync(llave, dispositivo: "DISP-NUEVO")).Content.ReadFromJsonAsync<JsonElement>();
        var score = json.GetProperty("score_ingreso").GetDouble();

        var estado = api.Services.GetRequiredService<IEstadoEnLinea>();
        var guardado = estado.ScoreIngresoVigente("C-LOGIN-ESCALA", DateTime.UtcNow, TimeSpan.FromMinutes(60));
        Assert.Equal(score, guardado);
        Assert.True(guardado >= 0.40); // aprobar con p >= 0,2 subiría a verificar
        Assert.Null(estado.ScoreIngresoVigente("C-LOGIN-ESCALA", DateTime.UtcNow.AddHours(3), TimeSpan.FromMinutes(60))); // vence
    }

    [Fact]
    public async Task Login_exitoso_y_registro_de_dispositivo_alimentan_el_estado_de_eventos()
    {
        var llave = await api.SembrarAsync("C-LOGIN-EVENTOS");
        var estado = api.Services.GetRequiredService<IEstadoEnLinea>();
        var ts = Tiempo.AEpochColombia(DateTime.UtcNow);
        Assert.False(estado.ConsultarLogin("C-LOGIN-EVENTOS", ts, "DISP-1", "Antioquia").DispositivoConocido); // el estado aún no vio el dispositivo

        Assert.Equal(HttpStatusCode.OK, (await api.LoginAsync(llave)).StatusCode);
        Assert.True(estado.ConsultarLogin("C-LOGIN-EVENTOS", ts, "DISP-1", "Antioquia").DispositivoConocido); // login exitoso = visto
        Assert.Equal(0, estado.ConsultarLogin("C-LOGIN-EVENTOS", ts, "DISP-1", "Antioquia").Fallidos1h);

        await api.LoginAsync(llave, "mala");
        Assert.Equal(1, estado.ConsultarLogin("C-LOGIN-EVENTOS", Tiempo.AEpochColombia(DateTime.UtcNow), "DISP-1", "Antioquia").Fallidos1h);
    }

    [Fact]
    public async Task Muchos_fallidos_con_dispositivo_nuevo_y_cambio_de_SIM_dan_bloqueo_temporal()
    {
        var llave = await api.SembrarAsync("C-LOGIN-FALLIDOS");
        var servicio = await api.ClienteAutenticadoAsync("SVC-LOGIN", Roles.Servicio);
        // 5 fallidos (0,36) + dispositivo nuevo (0,30) = 0,66 < 0,70; un cambio de SIM reciente (0,25) lleva el score a >= 0,91.
        var haceUnaHora = DateTime.UtcNow.AddHours(-6).ToString("yyyy-MM-dd HH:mm:ss");
        var ingesta = await servicio.PostAsJsonAsync("/v1/eventos", new
        {
            eventos = new[] { new { id_evento = "E-SIM-LOGIN", fecha_hora = haceUnaHora, id_cliente = "C-LOGIN-FALLIDOS", tipo_evento = "cambio_sim_detectado", resultado = "exitoso" } },
        });
        Assert.Equal(HttpStatusCode.OK, ingesta.StatusCode);
        for (var i = 0; i < 5; i++) await api.LoginAsync(llave, "mala");
        var json = await (await api.LoginAsync(llave, dispositivo: "DISP-OTRO")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("bloqueo_temporal", json.GetProperty("resultado").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("token").ValueKind);
    }

    [Fact]
    public async Task Cuerpo_invalido_devuelve_400()
    {
        var resp = await api.CreateClient().PostAsJsonAsync("/v1/auth/login", new { llave = "", clave = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Las_respuestas_llevan_cabeceras_seguras_y_correlacion()
    {
        var resp = await api.CreateClient().GetAsync("/v1/salud");
        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(resp.Headers.Contains("X-Correlation-Id"));
    }
}

public class LoginRateLimitTests(LoginRateLimitTests.FabricaLimite _api) : IClassFixture<LoginRateLimitTests.FabricaLimite>
{
    public class FabricaLimite() : FabricaApi(new() { ["Seguridad:Login:MaxIntentosPorLlave"] = "3" });

    [Fact]
    public async Task Supera_el_maximo_por_llave_y_recibe_429_con_Retry_After()
    {
        var llave = await _api.SembrarAsync("C-LIMITE");
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await _api.LoginAsync(llave, "mala")).StatusCode);

        var cuarto = await _api.LoginAsync(llave); // incluso con la clave correcta
        Assert.Equal(HttpStatusCode.TooManyRequests, cuarto.StatusCode);
        Assert.True(cuarto.Headers.Contains("Retry-After"));

        // Otra llave no se ve afectada
        var otra = await _api.SembrarAsync("C-LIMITE-2");
        Assert.Equal(HttpStatusCode.OK, (await _api.LoginAsync(otra)).StatusCode);
    }
}
