using System.Security.Claims;
using YumaBilletera.Api.Shared.Operaciones;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Transferencias.Consultar;

/// <summary>Estado de una operación propia (seguimiento: en_revision → completada | rechazada). Una operación ajena responde 404.</summary>
public sealed class ManejadorConsultarTransferencia(BilleteraDbContext db)
{
    public async Task<IResult> EjecutarAsync(string id, ClaimsPrincipal u, CancellationToken ct)
    {
        var idCliente = u.IdCliente();
        var t = await db.Transferencias.AsNoTracking().FirstOrDefaultAsync(x => x.IdTransaccion == id && x.IdClienteOrigen == idCliente, ct);
        if (t is null) return Results.NotFound();
        var v = t.Estado != EstadosTransferencia.RequiereVerificacion ? null
            : await db.Verificaciones.AsNoTracking()
                .Where(x => x.Proposito == PropositosVerificacion.Transferencia && x.Referencia == t.IdTransaccion && !x.Resuelta)
                .OrderByDescending(x => x.ExpiraUtc).FirstOrDefaultAsync(ct);
        return Results.Ok(OperacionResponse.De(t, v));
    }
}

public sealed class ConsultarTransferenciaFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorConsultarTransferencia>();

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapGet("/v1/transferencias/{id}", (string id, ClaimsPrincipal u, ManejadorConsultarTransferencia h, CancellationToken ct) => h.EjecutarAsync(id, u, ct))
            .RequireAuthorization(Politicas.Cliente).WithTags("Transferencias");
}
