using System.Text.Json;
using EscudoYuma.Api.Shared.Expediente;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Seguridad;
using Microsoft.EntityFrameworkCore;

namespace EscudoYuma.Api.Features.Alertas.Narrativa;

/// <param name="Origen">ollama | plantilla</param>
/// <param name="Desactualizada">true si el expediente cambió después de narrarlo (por ejemplo, llegó la resolución): conviene regenerar.</param>
public sealed record NarrativaResponse(
    Guid IdAlerta, string Origen, string Modelo, string VersionPrompt, bool Desactualizada, string? MotivoRespaldo, double LatenciaMs,
    DateTime CreadaUtc, JsonElement Contenido);

public sealed record NarrativaEnColaResponse(Guid IdAlerta, string Estado);

/// <summary>
/// Narrativa en lenguaje natural de una alerta (generada en segundo plano con Ollama, validada contra el expediente, o por plantilla).
/// GET devuelve la última; si aún no existe la encola y responde 202. POST .../regenerar la vuelve a pedir. Solo analista.
/// </summary>
public sealed class ManejadorNarrativa(EscudoDbContext db, ConstructorExpediente constructor, ColaNarrativas cola)
{
    public async Task<IResult> ObtenerAsync(Guid idAlerta, CancellationToken ct)
    {
        if (!await db.Alertas.AnyAsync(a => a.Id == idAlerta, ct)) return Results.Problem(title: "Alerta no encontrada", statusCode: 404);
        var n = await db.Narrativas.AsNoTracking().Where(x => x.IdAlerta == idAlerta).OrderByDescending(x => x.CreadaUtc).FirstOrDefaultAsync(ct);
        if (n is null)
        {
            cola.Encolar(idAlerta);
            return Results.Accepted($"/v1/alertas/{idAlerta}/narrativa", new NarrativaEnColaResponse(idAlerta, "en_cola"));
        }
        var actual = await constructor.PorAlertaAsync(idAlerta, ct);
        using var doc = JsonDocument.Parse(n.ContenidoJson);
        return Results.Ok(new NarrativaResponse(idAlerta, n.Origen, n.Modelo, n.VersionPrompt, actual is not null && actual.Hash != n.HashExpediente,
            n.MotivoRespaldo, Math.Round(n.LatenciaMs, 1), n.CreadaUtc, doc.RootElement.Clone()));
    }

    public async Task<IResult> RegenerarAsync(Guid idAlerta, CancellationToken ct)
    {
        if (!await db.Alertas.AnyAsync(a => a.Id == idAlerta, ct)) return Results.Problem(title: "Alerta no encontrada", statusCode: 404);
        cola.Encolar(idAlerta);
        return Results.Accepted($"/v1/alertas/{idAlerta}/narrativa", new NarrativaEnColaResponse(idAlerta, "en_cola"));
    }
}

public sealed class NarrativaFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorNarrativa>();

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        r.MapGet("/v1/alertas/{idAlerta:guid}/narrativa", (Guid idAlerta, ManejadorNarrativa h, CancellationToken ct) => h.ObtenerAsync(idAlerta, ct))
            .RequireAuthorization(Politicas.Analista).WithTags("Alertas");
        r.MapPost("/v1/alertas/{idAlerta:guid}/narrativa/regenerar", (Guid idAlerta, ManejadorNarrativa h, CancellationToken ct) => h.RegenerarAsync(idAlerta, ct))
            .RequireAuthorization(Politicas.Analista).WithTags("Alertas");
    }
}
