using System.Security.Claims;
using YumaBilletera.Api.Features.Destinatarios.Inscribir;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Destinatarios.Listar;

public sealed record DestinatariosResponse(IReadOnlyList<DestinatarioDto> Destinatarios);

/// <summary>Libreta de destinatarios del cliente autenticado.</summary>
public sealed class ManejadorListarDestinatarios(BilleteraDbContext db)
{
    public async Task<IResult> EjecutarAsync(ClaimsPrincipal usuario, CancellationToken ct)
    {
        var id = usuario.IdCliente();
        var lista = await db.Destinatarios.AsNoTracking().Where(d => d.IdCliente == id).OrderBy(d => d.Alias).ToListAsync(ct);
        return Results.Ok(new DestinatariosResponse(lista.Select(DestinatarioDto.De).ToList()));
    }
}

public sealed class ListarDestinatariosFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorListarDestinatarios>();

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapGet("/v1/destinatarios", (ClaimsPrincipal u, ManejadorListarDestinatarios h, CancellationToken ct) => h.EjecutarAsync(u, ct))
            .RequireAuthorization(Politicas.Cliente).WithTags("Destinatarios");
}
