using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EscudoYuma.Api.Shared.Notificaciones;
using EscudoYuma.Api.Shared.Persistencia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EscudoYuma.Api.Tests;

/// <summary>Rutas que usa la billetera (rol servicio) y el aviso firmado de resoluciones.</summary>
public class IntegracionBilleteraTests(FabricaApi api) : IClassFixture<FabricaApi>
{
    [Fact]
    public async Task Evaluar_ingreso_solo_servicio_y_mismo_score_que_el_login()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-ING", Roles.Servicio);
        var analista = await api.ClienteAutenticadoAsync("ANA-ING", Roles.Analista);
        var cuerpo = new { id_cliente = "C-ING", id_dispositivo = "DISP-1", departamento_ip = FabricaApi.Depto, dispositivos_conocidos = new[] { "DISP-1" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await analista.PostAsJsonAsync("/v1/auth/evaluar-ingreso", cuerpo)).StatusCode);

        var ok = await (await servicio.PostAsJsonAsync("/v1/auth/evaluar-ingreso", cuerpo)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("permitir", ok.GetProperty("resultado").GetString());

        // Dispositivo no visto + cambio de SIM reciente -> 0,30 + 0,25 = 0,55: otp (umbral 0,40), igual que /v1/auth/login.
        await servicio.PostAsJsonAsync("/v1/eventos", new
        {
            eventos = new[] { new { id_evento = "E-ING-SIM", fecha_hora = HoraColombiaAhora(-60), id_cliente = "C-ING", tipo_evento = "cambio_sim_detectado", resultado = "exitoso" } },
        });
        var otp = await (await servicio.PostAsJsonAsync("/v1/auth/evaluar-ingreso",
            new { id_cliente = "C-ING", id_dispositivo = "DISP-DESCONOCIDO", departamento_ip = FabricaApi.Depto, dispositivos_conocidos = new[] { "DISP-1" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("otp", otp.GetProperty("resultado").GetString());
        Assert.Equal(0.55, otp.GetProperty("score_ingreso").GetDouble(), 3);
    }

    [Fact]
    public async Task Desafio_fallido_crea_caso_pendiente_idempotente_y_consultable()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-DF", Roles.Servicio);
        var analista = await api.ClienteAutenticadoAsync("ANA-DF", Roles.Analista);
        var cuerpo = new { id_transaccion = "TY-DF-1", id_cliente = "C-DF", monto_cop = 480_000, metodo = "biometria", detalle = "El rostro no coincide", puntaje = 0.62, razones = new[] { "Destino nuevo" } };
        Assert.Equal(HttpStatusCode.NotFound, (await servicio.GetAsync("/v1/transacciones/TY-DF-1/decision")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await servicio.PostAsJsonAsync("/v1/alertas/desafio-fallido", cuerpo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await servicio.PostAsJsonAsync("/v1/alertas/desafio-fallido", cuerpo)).StatusCode); // idempotente

        var cola = await analista.GetFromJsonAsync<JsonElement>("/v1/alertas");
        var alerta = cola.GetProperty("alertas").EnumerateArray().Single(a => a.GetProperty("id_transaccion").GetString() == "TY-DF-1");
        Assert.Contains("biometría facial", alerta.GetProperty("razones")[0].GetString());

        await analista.PostAsJsonAsync($"/v1/alertas/{alerta.GetProperty("id").GetGuid()}/resolver", new { resolucion = "falso_positivo", accion = "liberar_transaccion" });
        var caso = await servicio.GetFromJsonAsync<JsonElement>("/v1/transacciones/TY-DF-1/decision");
        Assert.Equal(("resuelta", "falso_positivo", "liberar_transaccion"),
            (caso.GetProperty("estado_alerta").GetString(), caso.GetProperty("resolucion").GetString(), caso.GetProperty("accion").GetString()));
        Assert.False(caso.GetProperty("bloqueo_definitivo_cliente").GetBoolean());
    }

    [Fact]
    public async Task Aviso_de_resolucion_se_firma_y_llega_a_la_billetera()
    {
        var capturador = new Capturador();
        using var f = new FabricaApiConfig(new()
        {
            ["Billetera:UrlWebhook"] = "http://billetera.prueba/v1/internas/resoluciones",
            ["Billetera:SecretoWebhook"] = "secreto-webhook-solo-pruebas-0123456789-abc",
        });
        using var conHandler = f.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient(NotificadorResoluciones.ClienteHttp).ConfigurePrimaryHttpMessageHandler(() => capturador)));
        var servicio = await ((FabricaApi)f).ClienteAutenticadoAsync("SVC-WH", Roles.Servicio);
        _ = servicio;
        var sp = conHandler.Services;
        var notificador = sp.GetRequiredService<NotificadorResoluciones>();
        Assert.True(notificador.Habilitado);
        notificador.Encolar(new AvisoResolucion("TY-WH-1", "C-WH", "fraude_confirmado", "bloquear_cuenta", true, DateTime.UtcNow));

        var req = await capturador.Recibido.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cuerpo = capturador.Cuerpo!;
        var marca = long.Parse(req.Headers.GetValues(FirmaWebhook.CabeceraMarca).Single());
        Assert.Equal(FirmaWebhook.Firmar("secreto-webhook-solo-pruebas-0123456789-abc", marca, cuerpo), req.Headers.GetValues(FirmaWebhook.CabeceraFirma).Single());
        using var doc = JsonDocument.Parse(cuerpo);
        Assert.Equal("TY-WH-1", doc.RootElement.GetProperty("id_transaccion").GetString());
        Assert.True(doc.RootElement.GetProperty("bloqueo_definitivo").GetBoolean());
        _ = sp.GetRequiredService<IOptions<NotificacionesOptions>>();
    }

    private static string HoraColombiaAhora(int minutos) =>
        DateTime.UtcNow.AddHours(-5).AddMinutes(minutos).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Capturador : HttpMessageHandler
    {
        public TaskCompletionSource<HttpRequestMessage> Recibido { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Cuerpo { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Cuerpo = await request.Content!.ReadAsStringAsync(ct);
            Recibido.TrySetResult(request);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
