using System.Security.Claims;
using YumaBilletera.Api.Features.Dispositivos.Registrar;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Dispositivos.Listar;

public sealed record DispositivosResponse(IReadOnlyList<DispositivoDto> Dispositivos);

/// <summary>Dispositivos registrados del cliente autenticado, del más reciente al más antiguo.</summary>
public sealed class ManejadorListarDispositivos(BilleteraDbContext db)
{
    public async Task<IResult> EjecutarAsync(ClaimsPrincipal u, CancellationToken ct)
    {
        var id = u.IdCliente();
        var lista = await db.Dispositivos.AsNoTracking().Where(d => d.IdCliente == id).OrderByDescending(d => d.RegistradoUtc)
            .Select(d => new DispositivoDto(d.IdDispositivo, d.SistemaOperativo, d.RegistradoUtc)).ToListAsync(ct);
        return Results.Ok(new DispositivosResponse(lista));
    }
}

public sealed class ListarDispositivosFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorListarDispositivos>();

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapGet("/v1/dispositivos", (ClaimsPrincipal u, ManejadorListarDispositivos h, CancellationToken ct) => h.EjecutarAsync(u, ct))
            .RequireAuthorization(Politicas.Cliente).WithTags("Dispositivos");
}
