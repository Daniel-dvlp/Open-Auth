using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EscudoYuma.Api.Tests;

public class ScoringYAlertasTests(FabricaApi api) : IClassFixture<FabricaApi>
{
    private static readonly string[] Decisiones = ["aprobar", "desafiar", "retener", "rechazar"];

    private static async Task<JsonElement> EvaluarAsync(HttpClient c, Transaccion tx)
    {
        var resp = await c.PostAsJsonAsync("/v1/score/transaccion", tx, FabricaApi.Snake);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Evalua_y_devuelve_una_de_las_4_decisiones_con_razones_y_auditoria()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-1", Roles.Servicio);
        var tx = FabricaApi.Tx("C-SCORE-1");
        var json = await EvaluarAsync(servicio, tx);

        Assert.Contains(json.GetProperty("decision").GetString(), Decisiones);
        Assert.Equal("aprobar", json.GetProperty("decision").GetString());
        Assert.Equal("aprobar", json.GetProperty("decision_interna").GetString());
        Assert.NotEmpty(json.GetProperty("razones").EnumerateArray());
        Assert.True(json.GetProperty("latencia_ms").GetDouble() >= 0);
        Assert.Equal("xgb-onnx-1", json.GetProperty("version_modelo").GetString());
        Assert.True(json.GetProperty("latencia_ms").GetDouble() < 300);
        Assert.InRange(json.GetProperty("puntaje").GetDouble(), 0, 1);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EscudoDbContext>();
        Assert.True(await db.Auditoria.AnyAsync(r => r.Evento == "transaccion_evaluada" && r.Detalle.Contains(tx.IdTransaccion)));
    }

    [Fact]
    public async Task Retener_crea_alerta_y_la_cola_se_ordena_por_puntaje_por_monto()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-2", Roles.Servicio);
        var analista = await api.ClienteAutenticadoAsync("ANA-2", Roles.Analista);

        await FabricaApi.PrepararRiesgoAsync(servicio, "C-COLA-1");
        await FabricaApi.PrepararRiesgoAsync(servicio, "C-COLA-2");
        var chica = FabricaApi.Tx("C-COLA-1", monto: 900_000, saldo: 950_000, riesgosa: true);
        var grande = FabricaApi.Tx("C-COLA-2", monto: 1_900_000, saldo: 2_000_000, riesgosa: true);
        Assert.Equal("retener", (await EvaluarAsync(servicio, chica)).GetProperty("decision").GetString());
        Assert.Equal("retener", (await EvaluarAsync(servicio, grande)).GetProperty("decision").GetString());

        var cola = await analista.GetFromJsonAsync<JsonElement>("/v1/alertas?limite=200");
        var ids = cola.GetProperty("alertas").EnumerateArray().Select(a => a.GetProperty("id_transaccion").GetString()).ToList();
        Assert.Contains(chica.IdTransaccion, ids);
        Assert.True(ids.IndexOf(grande.IdTransaccion) < ids.IndexOf(chica.IdTransaccion));

        var prioridades = cola.GetProperty("alertas").EnumerateArray().Select(a => a.GetProperty("prioridad").GetDouble()).ToList();
        Assert.Equal(prioridades.OrderByDescending(p => p), prioridades);
    }

    [Fact]
    public async Task Retener_con_destino_de_fanin_alto_y_reputacion_baja_se_rechaza_y_explica_con_valores_concretos()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-3", Roles.Servicio);
        // Cuenta Yuma destino vinculada hace 5 días (antigüedad < 15 d: -25) que recibe de varios remitentes (fan-in: -15 por remitente extra).
        const string destino = "C-DEST-MULA";
        api.Services.GetRequiredService<IEstadoEnLinea>().CargarVinculaciones(
            [KeyValuePair.Create(destino, Tiempo.FechaAEpoch("2026-07-10"))]);

        JsonElement ultimo = default;
        for (var i = 1; i <= 5; i++) // el 5.º remitente ve fan-in 4 (reputación del destino = 100 - 45 - 25 = 30)
        {
            var cliente = $"C-MULA-{i}";
            await FabricaApi.PrepararRiesgoAsync(servicio, cliente);
            ultimo = await EvaluarAsync(servicio, FabricaApi.Tx(cliente, monto: 900_000, saldo: 950_000, riesgosa: true, idDestino: destino, tipoDestino: "cuenta_yuma"));
        }
        Assert.Equal("rechazar", ultimo.GetProperty("decision").GetString());
        Assert.Equal("rechazar", ultimo.GetProperty("decision_interna").GetString());
        var razones = ultimo.GetProperty("razones").EnumerateArray().Select(r => r.GetString()!).ToList();
        Assert.Contains(razones, r => r.Contains("remitentes distintos") && r.Contains("4"));
    }

    [Fact]
    public async Task Una_transaccion_comun_se_aprueba_con_una_razon_generica_y_una_riesgosa_trae_razones_con_valores()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-3B", Roles.Servicio);
        var normal = await EvaluarAsync(servicio, FabricaApi.Tx("C-NORMAL-1"));
        Assert.Equal("aprobar", normal.GetProperty("decision").GetString());
        Assert.Contains("Sin señales de riesgo relevantes", normal.GetProperty("razones")[0].GetString());

        await FabricaApi.PrepararRiesgoAsync(servicio, "C-RIESGO-1");
        var riesgosa = await EvaluarAsync(servicio, FabricaApi.TxRetener("C-RIESGO-1"));
        Assert.Equal("retener", riesgosa.GetProperty("decision").GetString());
        var razones = riesgosa.GetProperty("razones").EnumerateArray().Select(r => r.GetString()!).ToList();
        Assert.InRange(razones.Count, 1, 3);
        Assert.All(razones, r => Assert.False(string.IsNullOrWhiteSpace(r)));
        Assert.DoesNotContain(razones, r => r.Contains('{') || r.Contains("NaN"));
    }

    [Fact]
    public async Task La_reputacion_se_persiste_de_forma_asincrona_y_el_endpoint_devuelve_puntaje_y_senales()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-3C", Roles.Servicio);
        var analista = await api.ClienteAutenticadoAsync("ANA-3C", Roles.Analista);
        const string destino = "C-DEST-REP";
        api.Services.GetRequiredService<IEstadoEnLinea>().CargarVinculaciones([KeyValuePair.Create(destino, Tiempo.FechaAEpoch("2026-07-10"))]);
        foreach (var i in new[] { 1, 2, 3 })
            await EvaluarAsync(servicio, FabricaApi.Tx($"C-REP-{i}", idDestino: destino, tipoDestino: "cuenta_yuma"));

        // Eventualmente consistente: el escritor en segundo plano agrupa y persiste (hasta unos segundos).
        JsonElement rep = default;
        for (var intento = 0; intento < 100; intento++)
        {
            var resp = await analista.GetAsync($"/v1/cuentas/{destino}/reputacion");
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                rep = await resp.Content.ReadFromJsonAsync<JsonElement>();
                if (rep.GetProperty("puntaje").GetInt32() == 55) break; // pueden verse primero las instantáneas intermedias (fan-in 1, 2)
            }
            await Task.Delay(100);
        }
        Assert.Equal(JsonValueKind.Object, rep.ValueKind);
        // fan-in 3 en el estado tras la 3.ª tx (-30) + cuenta de 5 días (-25) => reputación 45 => riesgo 55
        Assert.Equal(55, rep.GetProperty("puntaje").GetInt32());
        Assert.Equal(45, rep.GetProperty("reputacion").GetInt32());
        var senales = rep.GetProperty("senales").EnumerateArray().Select(s => s.GetString()!).ToList();
        Assert.Contains(senales, s => s.Contains("remitentes distintos"));
        Assert.Contains(senales, s => s.Contains("menos de 15 días"));
    }

    [Fact]
    public async Task Transaccion_invalida_devuelve_400_y_sin_token_401_y_cliente_403()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-4", Roles.Servicio);
        var mala = FabricaApi.Tx("C-X") with { TipoOperacion = "inventada", MontoCop = -5 };
        Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsJsonAsync("/v1/score/transaccion", mala, FabricaApi.Snake)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await api.CreateClient().PostAsJsonAsync("/v1/score/transaccion", FabricaApi.Tx("C-X"), FabricaApi.Snake)).StatusCode);

        var cliente = await api.ClienteAutenticadoAsync("C-NORMAL", Roles.Cliente);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await cliente.PostAsJsonAsync("/v1/score/transaccion", FabricaApi.Tx("C-X"), FabricaApi.Snake)).StatusCode);
    }

    [Fact]
    public async Task Resolver_requiere_analista_y_retroalimenta_la_reputacion()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-5", Roles.Servicio);
        var analista = await api.ClienteAutenticadoAsync("ANA-5", Roles.Analista);
        await FabricaApi.PrepararRiesgoAsync(servicio, "C-RESOLVER");
        var tx = FabricaApi.TxRetener("C-RESOLVER");
        await EvaluarAsync(servicio, tx);
        var cola = await analista.GetFromJsonAsync<JsonElement>("/v1/alertas?limite=200");
        var id = cola.GetProperty("alertas").EnumerateArray().First(a => a.GetProperty("id_transaccion").GetString() == tx.IdTransaccion).GetProperty("id").GetString();

        // Un servicio no puede resolver
        Assert.Equal(HttpStatusCode.Forbidden,
            (await servicio.PostAsJsonAsync($"/v1/alertas/{id}/resolver", new { resolucion = "fraude_confirmado", accion = "mantener_retencion" })).StatusCode);
        // bloquear exige fraude confirmado
        Assert.Equal(HttpStatusCode.BadRequest,
            (await analista.PostAsJsonAsync($"/v1/alertas/{id}/resolver", new { resolucion = "falso_positivo", accion = "bloquear_cuenta" })).StatusCode);

        var ok = await analista.PostAsJsonAsync($"/v1/alertas/{id}/resolver", new { resolucion = "fraude_confirmado", accion = "bloquear_cuenta", comentario = "titular confirma" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var json = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("bloqueo_definitivo").GetBoolean());
        Assert.Equal(30, json.GetProperty("puntaje_reputacion").GetInt32());

        // Ya resuelta
        Assert.Equal(HttpStatusCode.Conflict,
            (await analista.PostAsJsonAsync($"/v1/alertas/{id}/resolver", new { resolucion = "falso_positivo", accion = "liberar_transaccion" })).StatusCode);

        // La reputación refleja el fraude y la cuenta bloqueada se rechaza en adelante
        var rep = await analista.GetFromJsonAsync<JsonElement>("/v1/cuentas/C-RESOLVER/reputacion");
        Assert.Equal(30, rep.GetProperty("puntaje").GetInt32());
        Assert.True(rep.GetProperty("bloqueo_definitivo").GetBoolean());
        Assert.Equal("rechazar", (await EvaluarAsync(servicio, FabricaApi.Tx("C-RESOLVER"))).GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Falso_positivo_baja_la_reputacion_sin_bloquear()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-6", Roles.Servicio);
        var analista = await api.ClienteAutenticadoAsync("ANA-6", Roles.Analista);
        await api.SembrarAsync("C-FP", llave: "k-c-fp");
        await FabricaApi.PrepararRiesgoAsync(servicio, "C-FP");
        var tx = FabricaApi.TxRetener("C-FP");
        await EvaluarAsync(servicio, tx);
        var cola = await analista.GetFromJsonAsync<JsonElement>("/v1/alertas?limite=200");
        var id = cola.GetProperty("alertas").EnumerateArray().First(a => a.GetProperty("id_transaccion").GetString() == tx.IdTransaccion).GetProperty("id").GetString();

        var json = await (await analista.PostAsJsonAsync($"/v1/alertas/{id}/resolver", new { resolucion = "falso_positivo", accion = "liberar_transaccion" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("bloqueo_definitivo").GetBoolean());
        Assert.Equal(0, json.GetProperty("puntaje_reputacion").GetInt32()); // 0 - 15 se recorta a 0
    }

    [Fact]
    public async Task Reputacion_de_cuenta_inexistente_es_404_y_un_cliente_no_ve_la_ajena()
    {
        var analista = await api.ClienteAutenticadoAsync("ANA-7", Roles.Analista);
        Assert.Equal(HttpStatusCode.NotFound, (await analista.GetAsync("/v1/cuentas/NO-EXISTE/reputacion")).StatusCode);

        var cliente = await api.ClienteAutenticadoAsync("C-PROPIO", Roles.Cliente);
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/v1/cuentas/C-PROPIO/reputacion")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.GetAsync("/v1/cuentas/OTRO/reputacion")).StatusCode);
    }

    [Fact]
    public async Task Metricas_prometheus_exponen_decisiones_y_latencia_por_endpoint()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-8", Roles.Servicio);
        await EvaluarAsync(servicio, FabricaApi.Tx("C-METRICAS"));
        var texto = await api.CreateClient().GetStringAsync("/metrics");
        Assert.Contains("yuma_decisiones_total{decision=\"aprobar\"}", texto);
        Assert.Contains("yuma_http_latencia_segundos_bucket{endpoint=\"/v1/score/transaccion\"", texto);
    }
}
