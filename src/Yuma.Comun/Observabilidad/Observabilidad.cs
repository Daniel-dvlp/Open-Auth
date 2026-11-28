using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Prometheus;
using Yuma.Comun.Auditoria;

namespace Yuma.Comun.Observabilidad;

/// <summary>Métrica HTTP común (registro estático por proceso, patrón estándar de prometheus-net). Cada servicio es un job distinto en Prometheus.</summary>
public static class MetricasHttp
{
    public static readonly Histogram LatenciaEndpoint = Metrics.CreateHistogram(
        "yuma_http_latencia_segundos", "Latencia de cada endpoint (plantilla de ruta, no URL real).",
        new HistogramConfiguration
        {
            LabelNames = ["endpoint", "metodo", "codigo"],
            Buckets = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5],
        });
}

/// <summary>Primer middleware: correlación (X-Correlation-Id) y contexto de auditoría; también mide latencia por endpoint.</summary>
public sealed class CorrelacionMetricasMiddleware(RequestDelegate siguiente)
{
    public const string Cabecera = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext ctx, ContextoAuditoria auditoria, ILogger<CorrelacionMetricasMiddleware> log)
    {
        var id = ctx.Request.Headers[Cabecera].FirstOrDefault();
        // Se acepta el id del llamador solo si es corto y alfanumérico (evita inyección en logs).
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            id = Guid.NewGuid().ToString("N");
        auditoria.IdCorrelacion = id;
        auditoria.Ip = ctx.Connection.RemoteIpAddress?.ToString();
        ctx.Response.Headers[Cabecera] = id;

        var sw = Stopwatch.StartNew();
        using var _ = log.BeginScope(new Dictionary<string, object> { ["IdCorrelacion"] = id });
        try { await siguiente(ctx); }
        finally
        {
            sw.Stop();
            var ruta = (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            if (ruta is not null && ruta != "/metrics") // sin endpoint = 404 de rutas arbitrarias: no se etiqueta (cardinalidad)
                MetricasHttp.LatenciaEndpoint.WithLabels(ruta, ctx.Request.Method, ctx.Response.StatusCode.ToString()).Observe(sw.Elapsed.TotalSeconds);
        }
    }
}

/// <summary>Errores no controlados -> ProblemDetails sin detalles internos (el detalle va al log con el id de correlación).</summary>
public sealed class ManejadorErrores(ILogger<ManejadorErrores> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        // Singleton: el contexto de auditoría (Scoped) se obtiene del request.
        var auditoria = ctx.RequestServices.GetRequiredService<ContextoAuditoria>();
        // Cuerpo ilegible (JSON mal formado/profundo, demasiado grande): error del cliente, no 500 (en Development Kestrel lo relanza como excepción).
        if (ex is BadHttpRequestException mala)
        {
            ctx.Response.StatusCode = mala.StatusCode is >= 400 and < 500 ? mala.StatusCode : StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = ctx.Response.StatusCode, Title = "Solicitud inválida", Detail = "El cuerpo de la solicitud no es válido.",
                Extensions = { ["id_correlacion"] = auditoria.IdCorrelacion },
            }, ct);
            return true;
        }
        log.LogError(ex, "Error no controlado. Correlación {IdCorrelacion}", auditoria.IdCorrelacion);
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = 500,
            Title = "Error interno",
            Detail = "Ocurrió un error inesperado. Cite el id de correlación al reportarlo.",
            Extensions = { ["id_correlacion"] = auditoria.IdCorrelacion },
        }, ct);
        return true;
    }
}

public static class LogsExtensions
{
    /// <summary>
    /// Logs JSON estructurados (una línea por evento, con scopes como IdCorrelacion) cuando Logging:Json=true (docker compose lo activa):
    /// facilitan buscar, ordenar y alimentar el expediente/observabilidad sin parsear texto.
    /// </summary>
    public static void UsarLogsJson(this Microsoft.Extensions.Logging.ILoggingBuilder logging, Microsoft.Extensions.Configuration.IConfiguration cfg)
    {
        if (!string.Equals(cfg["Logging:Json"], "true", StringComparison.OrdinalIgnoreCase)) return;
        logging.ClearProviders();
        logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });
    }
}
