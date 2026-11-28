using Prometheus;

namespace EscudoYuma.Api.Shared.Observabilidad;

/// <summary>Métricas Prometheus (registro estático por proceso, patrón estándar de prometheus-net).</summary>
public static class MetricasYuma
{
    public static readonly Counter Decisiones = Metrics.CreateCounter(
        "yuma_decisiones_total", "Decisiones de scoring por tipo.", "decision");

    public static readonly Counter DecisionesInternas = Metrics.CreateCounter(
        "yuma_decisiones_internas_total", "Decisiones internas (5 niveles).", "nivel");

    public static readonly Histogram LatenciaScoring = Metrics.CreateHistogram(
        "yuma_scoring_latencia_ms", "Latencia del pipeline de scoring en milisegundos.",
        new HistogramConfiguration { Buckets = [1, 2, 5, 10, 20, 50, 100, 250, 500, 1000] });

    public static readonly Counter Logins = Metrics.CreateCounter(
        "yuma_login_total", "Resultados de login.", "resultado");

    public static readonly Counter AlertasResueltas = Metrics.CreateCounter(
        "yuma_alertas_resueltas_total", "Alertas resueltas por resolución.", "resolucion");
}
