using EscudoYuma.Api.Shared.Expediente;
using EscudoYuma.Api.Shared.Seguridad;

namespace EscudoYuma.Api.Features.Alertas.ObtenerExpediente;

/// <summary>
/// Expediente de una alerta para el analista: hechos ordenados en el tiempo (operación, decisión con reglas y normas, eventos de la app,
/// ingresos, desafíos, resoluciones), reputación y normas aplicadas; cada hecho enlazado a su eslabón de auditoría. Solo analista.
/// </summary>
public sealed class ManejadorObtenerExpediente(ConstructorExpediente constructor)
{
    public async Task<IResult> EjecutarAsync(Guid idAlerta, CancellationToken ct) =>
        await constructor.PorAlertaAsync(idAlerta, ct) is { } e ? Results.Ok(e) : Results.Problem(title: "Alerta no encontrada", statusCode: 404);
}

public sealed class ObtenerExpedienteFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorObtenerExpediente>();

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapGet("/v1/alertas/{idAlerta:guid}/expediente", (Guid idAlerta, ManejadorObtenerExpediente h, CancellationToken ct) => h.EjecutarAsync(idAlerta, ct))
            .RequireAuthorization(Politicas.Analista).WithTags("Alertas");
}
