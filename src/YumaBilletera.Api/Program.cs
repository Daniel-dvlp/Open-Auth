using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using Prometheus;
using YumaBilletera.Api.Shared;
using YumaBilletera.Api.Shared.Escudo;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

// Program.cs solo compone: servicios transversales + features (vertical slice) + middlewares. Mismo orden que el Escudo.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.UsarLogsJson(builder.Configuration);

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = WebExtensions.MaxCuerpoBytes;
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    k.Limits.MaxRequestHeadersTotalSize = 16_384;
});
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; // contrato snake_case, igual que el Escudo
    o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ManejadorErrores>();
builder.Services.AddHttpContextAccessor();
builder.Services.TryAddSingleton(TimeProvider.System);

builder.Services.AddPersistencia();
builder.Services.AddSeguridad();
builder.Services.AddIntegracionEscudo();
builder.Services.AddNucleoBilletera(builder.Environment);
builder.Services.AddProxiesConfiables();
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; });
builder.Services.AddFeatures(typeof(Program).Assembly);
// Contrato OpenAPI para el equipo de frontend (Development u OpenApi:Habilitado=true): GET /openapi/v1.json
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Billetera Yuma API", Version = "v1",
        Description = "Billetera digital colombiana (COP, hora de Colombia). Cada operación que mueve dinero se califica antes en el Escudo Yuma. "
                    + "JSON en snake_case; errores como ProblemDetails; Authorization: Bearer <token>; Idempotency-Key obligatorio al mover dinero.",
    });
    o.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
    });
    o.CustomSchemaIds(t => t.FullName!.Replace("YumaBilletera.Api.", "").Replace('+', '.'));
});

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
// Documento de contrato (público, sin datos): antes de la autorización "denegar por defecto".
if (app.Environment.IsDevelopment() || app.Configuration.GetValue("OpenApi:Habilitado", false))
    app.UseSwagger(o => o.RouteTemplate = "openapi/{documentName}.json");
app.UseAuthentication();
app.UseAuthorization();

app.MapFeatures(typeof(Program).Assembly);
app.MapMetrics("/metrics").AllowAnonymous();

app.Run();

/// <summary>Necesario para WebApplicationFactory en las pruebas.</summary>
public partial class Program;
