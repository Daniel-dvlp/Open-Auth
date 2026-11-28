using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Seguridad;
using Microsoft.EntityFrameworkCore;

namespace EscudoYuma.Api.Features.Transacciones.ConsultarDecision;

/// <param name="EstadoAlerta">pendiente | resuelta (null si la operación no generó caso para analista).</param>
public sealed record ConsultarDecisionResponse(
    string IdTransaccion, string? EstadoAlerta, string? DecisionAlerta, string? Resolucion, string? Accion, bool BloqueoDefinitivoCliente, DateTime? ResueltaUtc);

/// <summary>
/// Conciliación para la billetera: estado del caso de una operación retenida (si un aviso de resolución se perdió, la billetera lo recupera
/// aquí). 404 si la operación no tiene caso. Solo rol servicio.
/// </summary>
public sealed class ManejadorConsultarDecision(EscudoDbContext db)
{
    public async Task<IResult> EjecutarAsync(string idTransaccion, CancellationToken ct)
    {
        var a = await db.Alertas.AsNoTracking().FirstOrDefaultAsync(x => x.IdTransaccion == idTransaccion, ct);
        if (a is null) return Results.NotFound();
        var bloqueo = await db.Reputaciones.AsNoTracking().Where(r => r.IdCuenta == a.IdCliente).Select(r => r.BloqueoDefinitivo).FirstOrDefaultAsync(ct);
        return Results.Ok(new ConsultarDecisionResponse(a.IdTransaccion, a.Estado, a.Decision, a.Resolucion, a.Accion, bloqueo, a.ResueltaUtc));
    }
}

public sealed class ConsultarDecisionFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorConsultarDecision>();

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapGet("/v1/transacciones/{idTransaccion}/decision", (string idTransaccion, ManejadorConsultarDecision h, CancellationToken ct) => h.EjecutarAsync(idTransaccion, ct))
            .RequireAuthorization(Politicas.Servicio).WithTags("Transacciones");
}
