using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using EscudoYuma.Api.Features.Salud;
using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Features;
using EscudoYuma.Api.Shared.Observabilidad;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Seguridad;
using Prometheus;

// Program.cs solo compone: servicios transversales + features (vertical slice) + middlewares.
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = SeguridadExtensions.MaxCuerpoBytes;                    // 1 MiB: un lote de 1.000 eventos pesa ~300 KB
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    k.Limits.MaxRequestHeadersTotalSize = 16_384;
});
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; // contrato en snake_case (openapi del sandbox)
    o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ManejadorErrores>();
builder.Services.AddHttpContextAccessor();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddScoped<ContextoAuditoria>();
builder.Services.AddSingleton<EstadoCadenaAuditoria>();
builder.Services.AddScoped<ServicioAuditoria>();

builder.Services.AddPersistencia();
builder.Services.AddSeguridad();
builder.Services.AddProxiesConfiables();
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; });
if (builder.Configuration["Redis:Conexion"] is { Length: > 0 } redis) builder.Services.AddRedisOpcional(redis);
builder.Services.AddFeatures();

var app = builder.Build();

app.UsePuertoMetricas(app.Configuration.GetValue("Seguridad:Metricas:Puerto", 0));
if (app.Configuration.GetSection("Seguridad:ProxiesConfiables").GetChildren().Any()) app.UseForwardedHeaders();
app.UseLimiteCuerpo();
app.UseExceptionHandler();
app.UseMiddleware<CorrelacionMetricasMiddleware>();
if (app.Configuration.GetValue("Seguridad:ForzarHttps", false))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseCabecerasSeguras();
app.UseAuthentication();
app.UseAuthorization();

app.MapFeatures();
app.MapMetrics("/metrics").AllowAnonymous(); // solo por el puerto interno Seguridad:Metricas:Puerto (ver UsePuertoMetricas)

app.Run();

/// <summary>Necesario para WebApplicationFactory en las pruebas.</summary>
public partial class Program;
