using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using EscudoYuma.Api.Shared.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prometheus;

namespace EscudoYuma.Api.Shared.Expediente;

/// <summary>Ollama local (sin costo, sin enviar datos fuera). Sin UrlBase se usa solo la narrativa por plantilla.</summary>
public sealed class OllamaOptions
{
    public string? UrlBase { get; set; }
    public string Modelo { get; set; } = "qwen2.5:7b-instruct";
    /// <summary>Un modelo de 7B en CPU tarda varios segundos: la narrativa es asíncrona y nunca está en el camino de una transacción.</summary>
    public int TimeoutSegundos { get; set; } = 120;
    public double Temperatura { get; set; } = 0.1;
    public bool Configurado => !string.IsNullOrWhiteSpace(UrlBase);
}

/// <summary>Salida validada del narrador (esquema fijo).</summary>
public sealed record ContenidoNarrativa(
    [property: JsonPropertyName("resumen")] string Resumen,
    [property: JsonPropertyName("linea_de_tiempo")] IReadOnlyList<PasoLinea> LineaDeTiempo,
    [property: JsonPropertyName("factores")] IReadOnlyList<string> Factores,
    [property: JsonPropertyName("normas_aplicadas")] IReadOnlyList<NormaAplicada> NormasAplicadas,
    [property: JsonPropertyName("recomendacion_al_analista")] string RecomendacionAlAnalista,
    [property: JsonPropertyName("confianza")] string Confianza);

public sealed record PasoLinea([property: JsonPropertyName("fecha")] string Fecha, [property: JsonPropertyName("hecho")] string Hecho);
public sealed record NormaAplicada([property: JsonPropertyName("codigo")] string Codigo, [property: JsonPropertyName("como_aplica")] string ComoAplica);

/// <summary>
/// Material a narrar: tipo (alerta | actividad_cliente), expediente serializado, normas que se pueden citar y fechas (hora de Colombia)
/// válidas para la línea de tiempo (null = no se exigen).
/// </summary>
public sealed record MaterialNarrable(string Tipo, string ExpedienteJson, IReadOnlyCollection<string> NormasPermitidas,
    IReadOnlyCollection<string>? FechasPermitidas = null, int MinPasosLinea = 0);

/// <summary>Puerto del narrador (Adapter). El LLM solo describe; la decisión ya la tomó el Escudo y la última palabra es del analista.</summary>
public interface INarrador
{
    string Modelo { get; }
    Task<ContenidoNarrativa> NarrarAsync(MaterialNarrable material, CancellationToken ct);
}

public sealed class NarracionInvalidaException(string motivo) : Exception(motivo);

/// <summary>
/// Adapter de Ollama (<c>POST /api/chat</c>, sin streaming) con salida restringida por esquema JSON y temperatura baja. La plantilla del
/// prompt es versionada (prompts/expediente_v1.md). Singleton (typed HttpClient).
/// </summary>
public sealed class OllamaNarrador(HttpClient http, IOptions<OllamaOptions> opciones, IHostEnvironment env) : INarrador
{
    public const string VersionPrompt = "expediente_v2";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly Lazy<string> _plantilla = new(() => File.ReadAllText(RutaPlantilla(env)));

    public string Modelo => opciones.Value.Modelo;

    public static string RutaPlantilla(IHostEnvironment env)
    {
        var enSalida = Path.Combine(AppContext.BaseDirectory, "prompts", VersionPrompt + ".md");
        return File.Exists(enSalida) ? enSalida : Path.Combine(env.ContentRootPath, "prompts", VersionPrompt + ".md");
    }

    private static readonly object Esquema = new
    {
        type = "object",
        required = new[] { "resumen", "linea_de_tiempo", "factores", "normas_aplicadas", "recomendacion_al_analista", "confianza" },
        properties = new Dictionary<string, object>
        {
            ["resumen"] = new { type = "string" },
            ["linea_de_tiempo"] = new { type = "array", items = new { type = "object", required = new[] { "fecha", "hecho" },
                properties = new { fecha = new { type = "string" }, hecho = new { type = "string" } } } },
            ["factores"] = new { type = "array", items = new { type = "string" } },
            ["normas_aplicadas"] = new { type = "array", items = new { type = "object", required = new[] { "codigo", "como_aplica" },
                properties = new { codigo = new { type = "string" }, como_aplica = new { type = "string" } } } },
            ["recomendacion_al_analista"] = new { type = "string" },
            ["confianza"] = new { type = "string", @enum = new[] { "alta", "media", "baja" } },
        },
    };

    public async Task<ContenidoNarrativa> NarrarAsync(MaterialNarrable material, CancellationToken ct)
    {
        var o = opciones.Value;
        if (!o.Configurado) throw new NarracionInvalidaException("Ollama no configurado (Ollama:UrlBase).");
        var prompt = _plantilla.Value.Replace("{{TIPO}}", material.Tipo).Replace("{{EXPEDIENTE}}", material.ExpedienteJson);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSegundos));
        using var resp = await http.PostAsJsonAsync(new Uri(new Uri(o.UrlBase!), "/api/chat"), new
        {
            model = o.Modelo, stream = false, format = Esquema,
            options = new { temperature = o.Temperatura },
            messages = new[] { new { role = "user", content = prompt } },
        }, cts.Token);
        if (!resp.IsSuccessStatusCode) throw new NarracionInvalidaException($"Ollama respondió {(int)resp.StatusCode}.");
        var cuerpo = await resp.Content.ReadFromJsonAsync<JsonElement>(cts.Token);
        var texto = cuerpo.GetProperty("message").GetProperty("content").GetString() ?? "";
        try { return JsonSerializer.Deserialize<ContenidoNarrativa>(texto, Json) ?? throw new NarracionInvalidaException("Respuesta vacía."); }
        catch (JsonException e) { throw new NarracionInvalidaException("La respuesta no es el JSON pedido: " + e.Message); }
    }
}

/// <summary>
/// Validación de la salida del LLM antes de mostrarla: campos obligatorios, normas citadas solo del expediente, sin datos personales y
/// sin cifras que no estén en el expediente (una cifra inventada es la alucinación más dañina para un analista).
/// </summary>
public static partial class ValidadorNarrativa
{
    [GeneratedRegex(@"\d[\d.,]*\d|\d")]
    private static partial Regex Numeros();
    [GeneratedRegex(@"\b3\d{9}\b")]
    private static partial Regex Celular();

    public static void Validar(ContenidoNarrativa n, MaterialNarrable m)
    {
        if (string.IsNullOrWhiteSpace(n.Resumen) || n.Resumen.Length > 2000) throw new NarracionInvalidaException("Resumen vacío o demasiado largo.");
        if (n.Confianza is not ("alta" or "media" or "baja")) throw new NarracionInvalidaException("Confianza inválida.");
        if (string.IsNullOrWhiteSpace(n.RecomendacionAlAnalista)) throw new NarracionInvalidaException("Falta la recomendación al analista.");
        if (n.Factores.Count > 8 || n.LineaDeTiempo.Count > 60) throw new NarracionInvalidaException("Demasiados elementos.");
        if (m.FechasPermitidas is not null && n.LineaDeTiempo.Any(p => !m.FechasPermitidas.Contains(p.Fecha.Trim())))
            throw new NarracionInvalidaException("La línea de tiempo usa fechas que no son las fecha_colombia del expediente.");
        if (n.LineaDeTiempo.Count < m.MinPasosLinea)
            throw new NarracionInvalidaException($"La línea de tiempo omite hechos ({n.LineaDeTiempo.Count} de al menos {m.MinPasosLinea}).");
        var ajenas = n.NormasAplicadas.Select(x => x.Codigo).Where(c => !m.NormasPermitidas.Contains(c)).ToList();
        if (ajenas.Count > 0) throw new NarracionInvalidaException("Cita normas que no están en el expediente: " + string.Join(", ", ajenas));

        var textos = new[] { n.Resumen, n.RecomendacionAlAnalista }.Concat(n.Factores).Concat(n.LineaDeTiempo.Select(p => p.Hecho))
            .Concat(n.NormasAplicadas.Select(x => x.ComoAplica)).ToList();
        if (textos.Any(t => Celular().IsMatch(t))) throw new NarracionInvalidaException("Incluye un número de celular.");
        var permitidas = CifrasDe(m.ExpedienteJson);
        var inventadas = textos.SelectMany(CifrasDe).Where(c => !permitidas.Contains(c)).Distinct().ToList();
        if (inventadas.Count > 0) throw new NarracionInvalidaException("Cita cifras que no están en el expediente: " + string.Join(", ", inventadas.Take(5)));
    }

    /// <summary>Cifras de 4 o más dígitos normalizadas (sin separadores de miles: "1.900.000" = "1900000"). Las pequeñas (horas, %) no se exigen.</summary>
    public static HashSet<string> CifrasDe(string texto)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match x in Numeros().Matches(texto))
        {
            var v = x.Value;
            var entero = Regex.Replace(v, @"[.,]\d{1,2}$", ""); // decimales cortos (0,62 / 12.5)
            var digitos = new string(entero.Where(char.IsDigit).ToArray());
            if (digitos.Length >= 4) res.Add(digitos.TrimStart('0') is { Length: > 0 } d ? d : "0");
            foreach (var parte in v.Split('-', ':', ' ', '/')) // partes de fechas y horas también valen ("2026-11-27")
            {
                var p = new string(parte.Where(char.IsDigit).ToArray());
                if (p.Length >= 4) res.Add(p.TrimStart('0') is { Length: > 0 } d2 ? d2 : "0");
            }
        }
        return res;
    }
}

/// <summary>Narrativa determinista por plantilla: respaldo cuando Ollama no está o su salida no pasa la validación. Siempre válida.</summary>
public static class NarradorPlantilla
{
    public const string Modelo = "plantilla-v1";
    private static string Cop(long v) => v.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');

    public static ContenidoNarrativa DeAlerta(ExpedienteAlerta e)
    {
        var linea = e.Hechos.Select(h => new PasoLinea(h.FechaColombia, DescribirHecho(h))).ToList();
        var factores = e.RazonesEscudo.Take(5).ToList();
        var normas = e.NormasAplicadas.Select(n => new NormaAplicada(n.Codigo, n.Descripcion)).ToList();
        var resumen = $"La operación {e.IdTransaccion} del cliente {e.IdCliente} por $ {Cop(e.MontoCop)} COP quedó en '{e.Decision}' "
                      + $"(puntaje del modelo {e.Puntaje.ToString("0.0000", CultureInfo.InvariantCulture)}). "
                      + (factores.Count > 0 ? "Motivo principal: " + factores[0] : "Sin razones registradas.");
        return new ContenidoNarrativa(resumen, linea, factores, normas,
            "Confirmar con el cliente, por un canal registrado, si reconoce la operación antes de resolver el caso.",
            e.Hechos.Count > 0 ? "media" : "baja");
    }

    public static ContenidoNarrativa DeActividad(string idCliente, IReadOnlyList<HechoDto> hechos, int dias)
    {
        var linea = hechos.Select(h => new PasoLinea(h.FechaColombia, DescribirHecho(h))).ToList();
        var porTipo = hechos.GroupBy(h => h.Tipo).Select(g => $"{g.Count()} {g.Key}").ToList();
        var normas = hechos.SelectMany(h => h.Normas).Distinct().Where(CatalogoNormas.Todas.ContainsKey)
            .Select(c => new NormaAplicada(c, CatalogoNormas.Todas[c].Descripcion)).ToList();
        return new ContenidoNarrativa(
            $"Actividad del cliente {idCliente} en los últimos {dias} días: " + (porTipo.Count > 0 ? string.Join(", ", porTipo) + "." : "sin hechos registrados."),
            linea, porTipo, normas, "Revisar los hechos con decisión distinta de 'aprobar' y las alertas abiertas.", hechos.Count > 0 ? "media" : "baja");
    }

    public static string DescribirHecho(HechoDto h)
    {
        string Campo(string n) => h.Datos.TryGetProperty(n, out var v) ? v.ToString() : "";
        return h.Tipo switch
        {
            TiposHecho.Transaccion => $"{Campo("tipo_operacion")} por $ {Campo("monto_cop")} hacia {Campo("tipo_destino")} {Campo("id_destino")}",
            TiposHecho.Decision => $"Escudo decidió {Campo("decision")} ({Campo("decision_interna")}), puntaje {Campo("puntaje")}",
            TiposHecho.EventoApp => $"Evento de la app: {Campo("tipo_evento")} ({Campo("resultado")})"
                                    + (string.IsNullOrEmpty(Campo("id_dispositivo")) ? "" : $" desde {Campo("id_dispositivo")}"),
            TiposHecho.Ingreso => $"Ingreso evaluado: {Campo("resultado")} (score {Campo("score_ingreso")})",
            TiposHecho.Alerta => $"Caso creado para analista ({Campo("decision")})",
            TiposHecho.DesafioFallido => $"Autenticación reforzada no superada ({Campo("metodo")})",
            TiposHecho.Resolucion => $"Analista resolvió: {Campo("resolucion")} / {Campo("accion")}",
            TiposHecho.CambioConfiguracion => $"Configuración vigente: modelo {Campo("version_modelo")}",
            TiposHecho.AlteracionAuditoria => $"Alteración detectada en la auditoría desde la secuencia {Campo("primera_secuencia_rota")}",
            _ => h.Tipo,
        };
    }
}

public static class MetricasNarrativa
{
    public static readonly Histogram Latencia = Metrics.CreateHistogram(
        "yuma_narrativa_latencia_segundos", "Tiempo de generación de narrativas con el LLM.",
        new HistogramConfiguration { Buckets = [0.5, 1, 2, 5, 10, 20, 40, 80, 160] });
    public static readonly Counter Respaldo = Metrics.CreateCounter(
        "yuma_narrativa_fallback_total", "Narrativas resueltas con la plantilla determinista.", "motivo");
}

/// <summary>Genera y guarda la narrativa de una alerta: LLM validado o, si falla, plantilla. Scoped.</summary>
public sealed class ServicioNarrativas(EscudoDbContext db, ConstructorExpediente constructor, INarrador narrador, TimeProvider reloj, ILogger<ServicioNarrativas> log)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = false };

    public async Task<NarrativaAlerta?> GenerarAsync(Guid idAlerta, CancellationToken ct)
    {
        var e = await constructor.PorAlertaAsync(idAlerta, ct);
        if (e is null) return null;
        var vista = VistaParaNarrar(e);
        var material = new MaterialNarrable("alerta", JsonSerializer.Serialize(vista, Json), e.NormasAplicadas.Select(n => n.Codigo).ToHashSet());
        var plantilla = NarradorPlantilla.DeAlerta(e);
        var (contenido, origen, modelo, motivo, ms) = await NarrarConRespaldoAsync(material, () => plantilla, ct, plantilla.LineaDeTiempo);
        var n = new NarrativaAlerta
        {
            IdAlerta = idAlerta, ContenidoJson = JsonSerializer.Serialize(contenido), Origen = origen, Modelo = modelo,
            VersionPrompt = OllamaNarrador.VersionPrompt, HashExpediente = e.Hash, LatenciaMs = ms, MotivoRespaldo = motivo,
            CreadaUtc = reloj.GetUtcNow().UtcDateTime,
        };
        db.Narrativas.Add(n);
        await db.SaveChangesAsync(ct);
        return n;
    }

    /// <summary>
    /// Lo que ve el LLM: sin ids internos ni fechas UTC (solo hora de Colombia), con el origen de la decisión ya calculado para que no lo
    /// deduzca (p. ej. una retención por regla de seguridad con puntaje del modelo bajo no debe narrarse como "alta probabilidad").
    /// </summary>
    public static object VistaParaNarrar(ExpedienteAlerta e)
    {
        var decision = e.Hechos.LastOrDefault(h => h.Tipo == TiposHecho.Decision);
        var piso = decision?.Normas.FirstOrDefault(n => n.StartsWith("PISO-", StringComparison.Ordinal));
        var origen = e.Hechos.Any(h => h.Tipo == TiposHecho.DesafioFallido) ? "autenticacion_reforzada_no_superada (el cliente no superó la biometría)"
            : decision?.Normas.Contains("NORMA-BLOQUEO-ANALISTA") == true && decision.Datos.TryGetProperty("cuenta_bloqueada", out var b) && b.GetBoolean()
                ? "cuenta con bloqueo definitivo previo"
            : piso is not null && CatalogoNormas.Todas.TryGetValue(piso, out var np) ? $"regla de seguridad {piso}: {np.Descripcion}"
            : "puntaje del modelo frente a los umbrales calibrados (POL-UMBRALES)";
        return new
        {
            id_transaccion = e.IdTransaccion, id_cliente = e.IdCliente, monto_cop = e.MontoCop.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.'),
            decision = e.Decision, puntaje_modelo = e.Puntaje, origen_decision = origen, estado_caso = e.Estado, razones_escudo = e.RazonesEscudo,
            reputacion = e.Reputacion,
            hechos = e.Hechos.Select(h => new { fecha_colombia = h.FechaColombia, tipo = h.Tipo, fuente = h.Fuente, datos = h.Datos, normas = h.Normas }),
            normas_aplicadas = e.NormasAplicadas,
        };
    }

    public async Task<(ContenidoNarrativa Contenido, string Origen, string Modelo, string? Motivo, double Ms)> NarrarConRespaldoAsync(
        MaterialNarrable material, Func<ContenidoNarrativa> plantilla, CancellationToken ct, IReadOnlyList<PasoLinea>? lineaDeterminista = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var c = await narrador.NarrarAsync(material, ct);
            // La línea de tiempo es un listado mecánico: la arma el sistema (exacta); el LLM aporta resumen, factores, normas y recomendación.
            if (lineaDeterminista is not null) c = c with { LineaDeTiempo = lineaDeterminista };
            ValidadorNarrativa.Validar(c, material);
            MetricasNarrativa.Latencia.Observe(sw.Elapsed.TotalSeconds);
            return (c, "ollama", narrador.Modelo, null, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when ((ex is NarracionInvalidaException or HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
        {
            var motivo = ex is NarracionInvalidaException ? ex.Message : "Ollama no disponible: " + ex.GetType().Name;
            MetricasNarrativa.Respaldo.WithLabels(ex is NarracionInvalidaException ? "validacion" : "no_disponible").Inc();
            log.LogInformation("Narrativa por plantilla ({Motivo}).", motivo);
            return (plantilla(), "plantilla", NarradorPlantilla.Modelo, motivo, sw.Elapsed.TotalMilliseconds);
        }
    }
}

/// <summary>Singleton: cola de alertas por narrar (se encola al crear una alerta). El GeneradorNarrativas la consume.</summary>
public sealed class ColaNarrativas
{
    private readonly Channel<Guid> _cola = Channel.CreateBounded<Guid>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
    internal ChannelReader<Guid> Lector => _cola.Reader;
    public void Encolar(Guid idAlerta) => _cola.Writer.TryWrite(idAlerta);
}

/// <summary>BackgroundService: genera las narrativas en orden, una a la vez (el LLM local es el recurso escaso).</summary>
public sealed class GeneradorNarrativas(ColaNarrativas cola, RegistroExpediente expediente, IServiceScopeFactory fabrica, ILogger<GeneradorNarrativas> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var id in cola.Lector.ReadAllAsync(ct))
        {
            // Espera breve a que los hechos de la operación lleguen a la BD (se escriben de forma asíncrona).
            for (var i = 0; i < 20 && expediente.Pendientes > 0; i++) await Task.Delay(100, ct);
            try
            {
                using var scope = fabrica.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ServicioNarrativas>().GenerarAsync(id, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { log.LogError(e, "No se pudo generar la narrativa de la alerta {IdAlerta}.", id); }
        }
    }
}

public static class ExpedienteExtensions
{
    public static IServiceCollection AddExpediente(this IServiceCollection s)
    {
        s.AddSingleton<RegistroExpediente>();
        s.AddHostedService<EscritorExpediente>();
        s.AddHostedService<HechoConfiguracionInicial>();
        s.AddSingleton<VerificadorCadena>();
        s.AddHostedService(sp => sp.GetRequiredService<VerificadorCadena>());
        s.AddScoped<ConstructorExpediente>();

        s.AddOptions<OllamaOptions>().BindConfiguration("Ollama");
        s.AddHttpClient(nameof(OllamaNarrador), c => c.Timeout = Timeout.InfiniteTimeSpan); // el plazo lo controla OllamaOptions
        s.AddSingleton<INarrador>(sp => new OllamaNarrador(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OllamaNarrador)),
            sp.GetRequiredService<IOptions<OllamaOptions>>(), sp.GetRequiredService<IHostEnvironment>()));
        s.AddScoped<ServicioNarrativas>();
        s.AddSingleton<ColaNarrativas>();
        s.AddHostedService<GeneradorNarrativas>();
        return s;
    }
}
