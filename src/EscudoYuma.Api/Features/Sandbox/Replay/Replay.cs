using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using EscudoYuma.Api.Features.Scoring.EvaluarTransaccion;
using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Scoring;
using EscudoYuma.Api.Shared.Seguridad;
using Microsoft.Extensions.Options;

namespace EscudoYuma.Api.Features.Sandbox.Replay;

public sealed class SandboxOptions
{
    public string UrlBase { get; set; } = "http://localhost:8080";
    /// <summary>Permite el replay (endpoint de administración y ejecución al iniciar). Desactivado por defecto: el replay REINICIA el estado en línea.</summary>
    public bool ReplayHabilitado { get; set; }
    /// <summary>Con <see cref="ReplayHabilitado"/>: ejecuta el replay completo al arrancar la aplicación (modo consola/CI).</summary>
    public bool EjecutarAlIniciar { get; set; }
    /// <summary>Transacciones pedidas al sandbox por lote. Pequeño (1-10) a propósito: el sandbox mide la latencia desde que entrega la transacción
    /// hasta que recibe su decisión, así que un lote grande suma la espera de todo el lote a cada decisión.</summary>
    public int TamanoLote { get; set; } = 10;
    /// <summary>Lotes que el productor puede pedir por adelantado mientras se califica/publica el actual (0 = secuencial: pedir, calificar y publicar
    /// cada lote antes de pedir el siguiente; es lo que menos latencia mide). Con >= 1 hay pipeline productor/consumidor; el orden se conserva.</summary>
    public int PrefetchLotes { get; set; }
    /// <summary>0 = hasta agotar el sandbox.</summary>
    public int MaxTransacciones { get; set; }
    public int PausaEntreLotesMs { get; set; }
    /// <summary>Carpeta 03_Datos (eventos_app.csv, clientes.csv, transacciones.csv). Relativa al directorio de contenido de la API.</summary>
    public string RutaDatos { get; set; } = "../../../Caso_de_estudio_Yuma_Pay/03_Datos";
    public string RutaReporte { get; set; } = "../../reports/replay_metricas.json";
    /// <summary>reporte_validacion.json de Python, para comparar. Opcional.</summary>
    public string RutaReferenciaPython { get; set; } = "../../training/artefactos/reporte_validacion.json";
    /// <summary>Periodo de evaluación local (validación del entrenamiento): sep-oct 2026, inclusive.</summary>
    public string EvaluacionDesde { get; set; } = "2026-09-01";
    public string EvaluacionHasta { get; set; } = "2026-10-31";
}

/// <summary>Decisión en el esquema `Decision` del sandbox (snake_case; decision en {aprobar, desafiar, retener, rechazar}).</summary>
public sealed record DecisionSandbox(string IdTransaccion, string Decision, double Puntaje, IReadOnlyList<string> Razones);
public sealed record LoteTransacciones(IReadOnlyList<Transaccion> Transacciones, int Restantes);

/// <summary>
/// ADAPTER del sandbox HTTP (mock_api.py): traduce el JSON snake_case en español del contrato openapi al dominio
/// y las decisiones del dominio al esquema `Decision`. Es un typed HttpClient (IHttpClientFactory gestiona el pool de conexiones).
/// </summary>
public sealed class SandboxClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    public async Task ReiniciarAsync(CancellationToken ct)
    {
        using var resp = await http.PostAsync("/v1/reiniciar", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<LoteTransacciones> SiguienteAsync(int cantidad, CancellationToken ct) =>
        await http.GetFromJsonAsync<LoteTransacciones>($"/v1/transacciones/siguiente?cantidad={cantidad}", Json, ct)
        ?? new LoteTransacciones([], 0);

    public async Task EnviarDecisionesAsync(IEnumerable<DecisionSandbox> decisiones, CancellationToken ct)
    {
        // Con Content-Length (no chunked): el sandbox solo lee el cuerpo cuando la cabecera existe.
        using var contenido = new StringContent(JsonSerializer.Serialize(decisiones, Json), System.Text.Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync("/v1/decisiones", contenido, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<JsonElement> MetricasAsync(CancellationToken ct) =>
        await http.GetFromJsonAsync<JsonElement>("/v1/metricas", Json, ct);

    public static DecisionSandbox ADecision(string idTransaccion, EvaluarTransaccionResponse r) =>
        new(idTransaccion, r.Decision, Math.Clamp(r.Puntaje, 0, 1), r.Razones);
}

/// <summary>
/// Lógica de lotes y orden del replay, aislada de HTTP y del pipeline de scoring para poder probarla.
/// Garantiza: (1) los lotes se califican y publican en el orden en que el sandbox los entregó (el estado en línea depende del orden cronológico),
/// (2) cada lote se publica en cuanto se califica (<c>procesar</c> hace ambas cosas antes de pasar al siguiente), (3) ningún lote supera
/// <c>tamanoLote</c> y no se piden más de <c>maxTx</c> transacciones en total.
/// Con <c>prefetch</c> = 0 es secuencial; con >= 1 un productor pide lotes por adelantado y un único consumidor califica y publica.
/// </summary>
public static class CanalLotes
{
    public static async Task EjecutarAsync(
        Func<int, CancellationToken, Task<LoteTransacciones>> pedir,
        Func<IReadOnlyList<Transaccion>, CancellationToken, Task> procesar,
        int tamanoLote, int maxTx, int prefetch, CancellationToken ct)
    {
        tamanoLote = Math.Max(1, tamanoLote);
        if (prefetch <= 0)
        {
            var hechas = 0;
            while (!ct.IsCancellationRequested)
            {
                var n = Cantidad(tamanoLote, maxTx, hechas);
                if (n <= 0) break;
                var lote = await pedir(n, ct);
                if (lote.Transacciones.Count == 0) break;
                await procesar(lote.Transacciones, ct);
                hechas += lote.Transacciones.Count;
                if (lote.Restantes == 0) break;
            }
            return;
        }

        // Pipeline: el canal acotado es el único punto de contacto; un lector y un escritor => el orden FIFO se conserva.
        var canal = System.Threading.Channels.Channel.CreateBounded<IReadOnlyList<Transaccion>>(
            new System.Threading.Channels.BoundedChannelOptions(prefetch) { SingleReader = true, SingleWriter = true });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var productor = Task.Run(async () =>
        {
            try
            {
                var pedidas = 0;
                while (!cts.IsCancellationRequested)
                {
                    var n = Cantidad(tamanoLote, maxTx, pedidas);
                    if (n <= 0) break;
                    var lote = await pedir(n, cts.Token);
                    if (lote.Transacciones.Count == 0) break;
                    pedidas += lote.Transacciones.Count;
                    await canal.Writer.WriteAsync(lote.Transacciones, cts.Token);
                    if (lote.Restantes == 0) break;
                }
                canal.Writer.TryComplete();
            }
            catch (Exception ex) { canal.Writer.TryComplete(ex); }
        }, CancellationToken.None);

        try
        {
            await foreach (var lote in canal.Reader.ReadAllAsync(cts.Token))
                await procesar(lote, cts.Token);
        }
        finally
        {
            await cts.CancelAsync(); // si el consumidor falla, el productor se detiene
            await productor;
        }
    }

    private static int Cantidad(int tamanoLote, int maxTx, int hechas) => maxTx > 0 ? Math.Min(tamanoLote, maxTx - hechas) : tamanoLote;
}

/// <summary>Una transacción calificada, con lo mínimo para evaluar localmente (la etiqueta se cruza aparte, nunca entra al pipeline).</summary>
public sealed record TxCalificada(string IdTransaccion, string FechaHora, string Decision, double Puntaje);
public sealed record EtiquetaTx(bool EsFraude, string Modalidad, long Monto);

/// <summary>Métricas locales: mismas definiciones que training/evaluar.py (modelo + política), calculadas sobre un periodo.</summary>
public static class MetricasLocales
{
    public static readonly string[] Modalidades = ["toma_de_cuenta", "sim_swapping", "ingenieria_social", "engano_qr", "cuenta_mula"];

    public static Dictionary<string, object?> Calcular(IEnumerable<TxCalificada> calificadas, IReadOnlyDictionary<string, EtiquetaTx> etiquetas, string desde, string hasta)
    {
        // hasta inclusive: la fecha_hora es "AAAA-MM-DD HH:MM:SS", se compara contra el día siguiente
        var limite = DateTime.ParseExact(hasta, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var filas = calificadas.Where(c => string.CompareOrdinal(c.FechaHora, desde) >= 0 && string.CompareOrdinal(c.FechaHora, limite) < 0)
            .Where(c => etiquetas.ContainsKey(c.IdTransaccion)).Select(c => (c.Decision, Et: etiquetas[c.IdTransaccion])).ToList();

        var fr = filas.Where(f => f.Et.EsFraude).ToList();
        var lg = filas.Where(f => !f.Et.EsFraude).ToList();
        static bool Detecta(string d) => d != "aprobar";               // desafiar, retener o rechazar
        static bool Bloquea(string d) => d is "retener" or "rechazar"; // el dinero no sale
        double Pct(double a, double b) => b > 0 ? 100.0 * a / b : 0;

        var vpDet = fr.Count(f => Detecta(f.Decision));
        var vpBlq = fr.Count(f => Bloquea(f.Decision));
        var fp = lg.Count(f => Bloquea(f.Decision));
        double montoFraude = fr.Sum(f => (double)f.Et.Monto);
        double montoDet = fr.Where(f => Detecta(f.Decision)).Sum(f => (double)f.Et.Monto);

        return new Dictionary<string, object?>
        {
            ["periodo"] = $"{desde}..{hasta}",
            ["n_tx"] = filas.Count,
            ["n_fraudes"] = fr.Count,
            ["n_legitimas"] = lg.Count,
            // definición de evaluar.py: FP% = fp / (fraudes retenidos/rechazados + fp)
            ["fp_pct"] = Pct(fp, vpBlq + fp),
            // definición del sandbox: FP% = fp / (fraudes detectados incl. desafiados + fp)
            ["fp_pct_sandbox"] = Pct(fp, vpDet + fp),
            ["deteccion_casos_pct"] = Pct(vpDet, fr.Count),
            ["deteccion_monto_pct"] = Pct(montoDet, montoFraude),
            ["deteccion_por_modalidad_pct"] = Modalidades.ToDictionary(m => m, m =>
            {
                var fm = fr.Where(f => f.Et.Modalidad == m).ToList();
                return (object?)Pct(fm.Count(f => Detecta(f.Decision)), fm.Count);
            }),
            ["fraude_retenido_o_rechazado_pct"] = Pct(vpBlq, fr.Count),
            ["legitimas_desafiadas_pct"] = Pct(lg.Count(f => f.Decision == "desafiar"), lg.Count),
            ["legitimas_retenidas_pct"] = Pct(lg.Count(f => f.Decision == "retener"), lg.Count),
            ["legitimas_rechazadas_pct"] = Pct(lg.Count(f => f.Decision == "rechazar"), lg.Count),
            ["legitimas_bloqueadas_pct"] = Pct(fp, lg.Count),
            ["n_retenidas"] = filas.Count(f => f.Decision == "retener"),
            ["n_rechazadas"] = filas.Count(f => f.Decision == "rechazar"),
            ["n_desafiadas"] = filas.Count(f => f.Decision == "desafiar"),
            ["fp_en_rechazadas"] = lg.Count(f => f.Decision == "rechazar"),
        };
    }

    /// <summary>Percentil por rango más cercano, igual que el sandbox: sorted[min(n-1, int(n*q))].</summary>
    public static double Percentil(double[] ordenados, double q) =>
        ordenados.Length == 0 ? 0 : ordenados[Math.Min(ordenados.Length - 1, (int)(ordenados.Length * q))];
}

/// <summary>Avance del replay (consultable por GET /v1/sandbox/replay).</summary>
public sealed class EstadoReplay
{
    public string Estado { get; set; } = "inactivo"; // inactivo | ejecutando | terminado | error
    public int Procesadas { get; set; }
    public DateTime? InicioUtc { get; set; }
    public DateTime? FinUtc { get; set; }
    public string? Mensaje { get; set; }
    public string? RutaReporte { get; set; }
}

/// <summary>
/// Replay contra el sandbox: reinicia el sandbox y el estado en línea, alimenta el estado con los eventos de app anteriores a cada
/// transacción (leyendo eventos_app.csv por orden: sin información del futuro), califica cada transacción con el MISMO pipeline del
/// endpoint (<see cref="ManejadorEvaluarTransaccion"/>), publica las decisiones en lotes y al final guarda las métricas del sandbox,
/// las métricas locales sep-oct (cruzando con transacciones.csv: es_fraude SOLO se usa para evaluar) y las latencias internas.
/// Singleton: una sola ejecución a la vez; cada lote usa su propio scope (el DbContext es Scoped).
/// </summary>
public sealed class ServicioReplay(IServiceScopeFactory fabrica, IEstadoEnLinea estado, IOptions<SandboxOptions> opciones,
    IHostEnvironment env, ILogger<ServicioReplay> log)
{
    private static readonly JsonSerializerOptions JsonSalida = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private readonly object _candado = new();
    private Task? _tarea;
    public EstadoReplay Estado { get; } = new();

    private string Resolver(string ruta) => Path.IsPathRooted(ruta) ? ruta : Path.GetFullPath(Path.Combine(env.ContentRootPath, ruta));

    /// <summary>Inicia el replay en segundo plano. Devuelve false si ya hay uno en curso.</summary>
    public bool Iniciar(CancellationToken ct)
    {
        lock (_candado)
        {
            if (_tarea is { IsCompleted: false }) return false;
            Estado.Estado = "ejecutando"; Estado.Procesadas = 0; Estado.Mensaje = null; Estado.InicioUtc = DateTime.UtcNow; Estado.FinUtc = null;
            _tarea = Task.Run(() => EjecutarAsync(ct), CancellationToken.None);
            return true;
        }
    }

    public Task? Tarea => _tarea;

    public async Task EjecutarAsync(CancellationToken ct)
    {
        try
        {
            var reporte = await EjecutarInternoAsync(ct);
            Estado.Estado = "terminado"; Estado.RutaReporte = reporte;
        }
        catch (OperationCanceledException) { Estado.Estado = "error"; Estado.Mensaje = "cancelado"; }
        catch (Exception ex)
        {
            Estado.Estado = "error"; Estado.Mensaje = ex.Message;
            log.LogError(ex, "Replay del sandbox interrumpido tras {Total} transacciones.", Estado.Procesadas);
        }
        finally { Estado.FinUtc = DateTime.UtcNow; }
    }

    private async Task<string> EjecutarInternoAsync(CancellationToken ct)
    {
        var opc = opciones.Value;
        var datos = Resolver(opc.RutaDatos);
        log.LogInformation("Replay del sandbox iniciado (lote {Lote}, prefetch {Prefetch}, datos {Datos}).", opc.TamanoLote, opc.PrefetchLotes, datos);

        // Datos estáticos y eventos (nunca la etiqueta: esa se carga aparte, solo para evaluar al final).
        var vinculaciones = LectorCsv.Leer(Path.Combine(datos, "clientes.csv"))
            .Select(f => KeyValuePair.Create(f["id_cliente"], Tiempo.FechaAEpoch(f["fecha_vinculacion"]))).ToList();
        var eventos = LectorCsv.Leer(Path.Combine(datos, "eventos_app.csv"))
            .Select(f => new EventoApp(f["id_evento"], f["fecha_hora"], f["id_cliente"], f["tipo_evento"], f["id_dispositivo"], f["sistema_operativo"], f["departamento_ip"], f["resultado"]))
            .OrderBy(e => e.FechaHora, StringComparer.Ordinal).ThenBy(e => e.IdEvento, StringComparer.Ordinal).ToList();

        // Reinicio: sandbox (cursor y decisiones) y estado en línea (ventanas, eventos, sesiones).
        using (var scope0 = fabrica.CreateScope())
            await scope0.ServiceProvider.GetRequiredService<SandboxClient>().ReiniciarAsync(ct);
        estado.Reiniciar();
        estado.CargarVinculaciones(vinculaciones);

        var calificadas = new List<TxCalificada>(130_000);
        var latScoring = new List<double>(130_000);
        var latTotal = new List<double>(130_000);
        var porDecision = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var ie = 0;
        var procesadas = 0;
        var reloj = Stopwatch.StartNew();

        // Un único cliente tipado para todo el replay (cada petición abre su conexión porque el mock es HTTP/1.0).
        using var scopeCliente = fabrica.CreateScope();
        var cliente = scopeCliente.ServiceProvider.GetRequiredService<SandboxClient>();

        // Cada lote pequeño se califica y se publica de inmediato (la latencia del sandbox es entrega -> decision). El orden cronológico del
        // estado se conserva porque los lotes se procesan uno tras otro en el orden de entrega.
        async Task Procesar(IReadOnlyList<Transaccion> transacciones, CancellationToken token)
        {
            using var scope = fabrica.CreateScope();
            scope.ServiceProvider.GetRequiredService<ContextoAuditoria>().Actor = "replay-sandbox";
            var manejador = scope.ServiceProvider.GetRequiredService<ManejadorEvaluarTransaccion>();
            var decisiones = new List<DecisionSandbox>(transacciones.Count);
            foreach (var tx in transacciones)
            {
                // Solo eventos estrictamente anteriores a la transacción: a igual segundo la tx va primero (orden de features.py).
                var ts = tx.Ts;
                while (ie < eventos.Count && eventos[ie].Ts < ts) estado.ProcesarEvento(eventos[ie++]);

                var r = await manejador.EjecutarAsync(tx, token);
                decisiones.Add(SandboxClient.ADecision(tx.IdTransaccion, r));
                calificadas.Add(new TxCalificada(tx.IdTransaccion, tx.FechaHora, r.Decision, r.Puntaje));
                latScoring.Add(r.LatenciaMs);
                latTotal.Add(r.LatenciaTotalMs);
                porDecision[r.Decision] = porDecision.GetValueOrDefault(r.Decision) + 1;
            }
            await cliente.EnviarDecisionesAsync(decisiones, token);

            procesadas += decisiones.Count;
            Estado.Procesadas = procesadas;
            if (procesadas % 5_000 < decisiones.Count) log.LogInformation("Replay: {Total} transacciones ({Seg:0} s).", procesadas, reloj.Elapsed.TotalSeconds);
            if (opc.PausaEntreLotesMs > 0) await Task.Delay(opc.PausaEntreLotesMs, token);
        }

        await CanalLotes.EjecutarAsync(cliente.SiguienteAsync, Procesar, opc.TamanoLote, opc.MaxTransacciones, opc.PrefetchLotes, ct);
        reloj.Stop();

        // Métricas del sandbox (todo lo decidido) y locales sep-oct (cruzando con la etiqueta, solo para evaluar).
        JsonElement metricasSandbox;
        using (var scope1 = fabrica.CreateScope())
            metricasSandbox = await scope1.ServiceProvider.GetRequiredService<SandboxClient>().MetricasAsync(ct);

        var etiquetas = LectorCsv.Leer(Path.Combine(datos, "transacciones.csv")).ToDictionary(
            f => f["id_transaccion"], f => new EtiquetaTx(f["es_fraude"] == "1", f["modalidad_fraude"], long.Parse(f["monto_cop"], System.Globalization.CultureInfo.InvariantCulture)));
        var locales = MetricasLocales.Calcular(calificadas, etiquetas, opc.EvaluacionDesde, opc.EvaluacionHasta);

        var ls = latScoring.OrderBy(x => x).ToArray();
        var lt = latTotal.OrderBy(x => x).ToArray();
        var latencias = new Dictionary<string, object?>
        {
            ["transacciones"] = ls.Length,
            ["scoring_interno_ms_p50"] = Math.Round(MetricasLocales.Percentil(ls, 0.50), 4),
            ["scoring_interno_ms_p99"] = Math.Round(MetricasLocales.Percentil(ls, 0.99), 4),
            ["scoring_interno_ms_max"] = Math.Round(ls.Length > 0 ? ls[^1] : 0, 4),
            ["pipeline_total_ms_p50"] = Math.Round(MetricasLocales.Percentil(lt, 0.50), 4),
            ["pipeline_total_ms_p99"] = Math.Round(MetricasLocales.Percentil(lt, 0.99), 4),
            ["nota"] = "scoring_interno = estado + atributos + ONNX + politica + razones (Stopwatch); pipeline_total suma auditoria y alerta. "
                       + "La latencia del sandbox mide entrega->decision: incluye la espera del lote y el POST de cada lote.",
            ["lote_tamano"] = opc.TamanoLote,
            ["prefetch_lotes"] = opc.PrefetchLotes,
        };

        var referencia = LeerReferencia(opc);
        var gates = Gates(locales, referencia, ls);
        var reporteJson = new Dictionary<string, object?>
        {
            ["generado_utc"] = DateTime.UtcNow,
            ["transacciones_procesadas"] = procesadas,
            ["duracion_s"] = Math.Round(reloj.Elapsed.TotalSeconds, 1),
            ["tx_por_segundo"] = Math.Round(procesadas / Math.Max(reloj.Elapsed.TotalSeconds, 1e-9), 1),
            ["decisiones"] = porDecision,
            ["metricas_sandbox_periodo_completo"] = metricasSandbox,
            ["metricas_locales_sep_oct"] = locales,
            ["latencias_internas"] = latencias,
            ["referencia_python_sep_oct"] = referencia is null ? null : new { linea_base = referencia.Value.Base, modelo_politica = referencia.Value.Modelo },
            ["diferencia_vs_python"] = referencia is null ? null : Diferencias(locales, referencia.Value.Modelo),
            ["gates"] = gates,
        };
        var ruta = Resolver(opc.RutaReporte);
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        await File.WriteAllTextAsync(ruta, JsonSerializer.Serialize(reporteJson, JsonSalida), ct);
        log.LogInformation("Replay terminado: {Total} transacciones en {Seg:0.0} s; reporte en {Ruta}.", procesadas, reloj.Elapsed.TotalSeconds, ruta);
        return ruta;
    }

    private (JsonElement Base, JsonElement Modelo)? LeerReferencia(SandboxOptions opc)
    {
        var ruta = Resolver(opc.RutaReferenciaPython);
        if (!File.Exists(ruta)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(ruta));
        return (doc.RootElement.GetProperty("linea_base").Clone(), doc.RootElement.GetProperty("modelo_politica").Clone());
    }

    private static Dictionary<string, object?> Diferencias(Dictionary<string, object?> local, JsonElement py)
    {
        var d = new Dictionary<string, object?>();
        foreach (var k in new[] { "fp_pct", "deteccion_casos_pct", "deteccion_monto_pct", "fraude_retenido_o_rechazado_pct", "legitimas_desafiadas_pct",
                     "legitimas_retenidas_pct", "legitimas_rechazadas_pct", "n_retenidas", "n_rechazadas", "n_desafiadas", "fp_en_rechazadas" })
            if (py.TryGetProperty(k, out var v) && local.TryGetValue(k, out var l))
                d[k] = Math.Round(Convert.ToDouble(l, System.Globalization.CultureInfo.InvariantCulture) - v.GetDouble(), 4);
        if (py.TryGetProperty("deteccion_por_modalidad_pct", out var pm) && local["deteccion_por_modalidad_pct"] is Dictionary<string, object?> lm)
            d["deteccion_por_modalidad_pct"] = lm.ToDictionary(kv => kv.Key,
                kv => (object?)Math.Round(Convert.ToDouble(kv.Value, System.Globalization.CultureInfo.InvariantCulture) - pm.GetProperty(kv.Key).GetDouble(), 4));
        return d;
    }

    private static Dictionary<string, object?> Gates(Dictionary<string, object?> local, (JsonElement Base, JsonElement Modelo)? referencia, double[] latScoring)
    {
        var fp = Convert.ToDouble(local["fp_pct"], System.Globalization.CultureInfo.InvariantCulture);
        var det = Convert.ToDouble(local["deteccion_casos_pct"], System.Globalization.CultureInfo.InvariantCulture);
        var baseDet = referencia?.Base.GetProperty("deteccion_casos_pct").GetDouble();
        var p99 = MetricasLocales.Percentil(latScoring, 0.99);
        return new Dictionary<string, object?>
        {
            ["fp_sep_oct_menor_60_pct"] = fp < 60,
            ["deteccion_sep_oct_mayor_o_igual_linea_base"] = baseDet is null ? null : det >= baseDet,
            ["latencia_interna_p99_menor_300_ms"] = p99 < 300,
        };
    }
}

/// <summary>Con Sandbox:ReplayHabilitado=true y Sandbox:EjecutarAlIniciar=true corre el replay completo al arrancar (modo consola).</summary>
public sealed class SandboxReplayService(ServicioReplay replay, IOptions<SandboxOptions> opciones, IHostApplicationLifetime vida) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opc = opciones.Value;
        if (!opc.ReplayHabilitado || !opc.EjecutarAlIniciar) return;
        await Task.Yield();
        await replay.EjecutarAsync(ct);
        if (replay.Estado.Estado == "error") Environment.ExitCode = 1;
        if (!ct.IsCancellationRequested) vida.StopApplication(); // modo consola: termina al acabar el replay (código de salida 1 si falló)
    }
}

public sealed class ReplayFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddOptions<SandboxOptions>().BindConfiguration("Sandbox");
        s.AddHttpClient<SandboxClient>((sp, c) =>
        {
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<SandboxOptions>>().Value.UrlBase);
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.ConnectionClose = true; // el sandbox responde HTTP/1.0 y cierra cada conexión: no reutilizarlas (un POST no se reintenta)
        });
        s.AddSingleton<ServicioReplay>();
        s.AddHostedService<SandboxReplayService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        // Administración (rol analista): lanzar el replay y consultar su avance. Requiere Sandbox:ReplayHabilitado=true.
        r.MapPost("/v1/sandbox/replay", (ServicioReplay replay, IOptions<SandboxOptions> opc, CancellationToken _) =>
            {
                if (!opc.Value.ReplayHabilitado)
                    return Results.Problem(title: "Replay deshabilitado", detail: "Active Sandbox:ReplayHabilitado para ejecutarlo.", statusCode: 409);
                return replay.Iniciar(CancellationToken.None)
                    ? Results.Accepted("/v1/sandbox/replay", replay.Estado)
                    : Results.Problem(title: "Replay en curso", detail: "Ya hay un replay ejecutándose.", statusCode: 409);
            })
            .RequireAuthorization(Politicas.Analista).WithTags("Sandbox");
        r.MapGet("/v1/sandbox/replay", (ServicioReplay replay) => Results.Ok(replay.Estado))
            .RequireAuthorization(Politicas.Analista).WithTags("Sandbox");
    }
}
