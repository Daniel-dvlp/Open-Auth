using System.Text.Json;
using Microsoft.Extensions.Options;
using YumaBilletera.Api.Shared.Escudo;

namespace YumaBilletera.Api.Features.Internas.AplicarResolucion;

/// <summary>
/// Aviso del Escudo: un analista resolvió el caso de una operación retenida. No usa token de cliente: se autentica con firma HMAC-SHA256
/// del cuerpo y una marca de tiempo (ventana de 5 min) con el secreto compartido Escudo:SecretoWebhook. Sin secreto configurado responde 404.
/// </summary>
public sealed class ManejadorAplicarResolucion(AplicadorResoluciones aplicador, IOptions<EscudoOptions> opciones, TimeProvider reloj, ILogger<ManejadorAplicarResolucion> log)
{
    public const string CabeceraFirma = "X-Yuma-Firma";
    public const string CabeceraMarca = "X-Yuma-Marca-Tiempo";

    public async Task<IResult> EjecutarAsync(HttpContext http, CancellationToken ct)
    {
        var o = opciones.Value;
        if (o.SecretoWebhook is not { Length: >= 32 }) return Results.NotFound();
        using var lector = new StreamReader(http.Request.Body);
        var cuerpo = await lector.ReadToEndAsync(ct);
        if (!AplicadorResoluciones.FirmaValida(o.SecretoWebhook, http.Request.Headers[CabeceraMarca], http.Request.Headers[CabeceraFirma], cuerpo,
                reloj.GetUtcNow(), o.SegundosTolerancia))
        {
            log.LogWarning("Aviso de resolución con firma inválida o vencida.");
            return Results.Unauthorized();
        }
        AvisoResolucion? aviso;
        try { aviso = JsonSerializer.Deserialize<AvisoResolucion>(cuerpo); }
        catch (JsonException) { return Results.BadRequest(); }
        if (aviso is null || string.IsNullOrEmpty(aviso.IdTransaccion)) return Results.BadRequest();
        if (aviso.Resolucion is not ("falso_positivo" or "fraude_confirmado")) return Results.BadRequest();

        var existe = await aplicador.AplicarAsync(aviso.IdTransaccion, aviso.Resolucion, aviso.Accion, aviso.BloqueoDefinitivo, ct);
        return existe ? Results.NoContent() : Results.Ok(); // idempotente: un aviso repetido o de una operación ya cerrada no es error
    }
}

public sealed class AplicarResolucionFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorAplicarResolucion>();

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/internas/resoluciones", (HttpContext http, ManejadorAplicarResolucion h, CancellationToken ct) => h.EjecutarAsync(http, ct))
            .AllowAnonymous().WithTags("Internas");
}
