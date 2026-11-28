using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Sesiones;

namespace YumaBilletera.Api.Features.Auth.Refrescar;

public sealed record RefrescarRequest(string RefreshToken);
public sealed record RefrescarResponse(string Token, DateTime ExpiraUtc, string RefreshToken, DateTime RefreshExpiraUtc);

public sealed class RefrescarValidator : AbstractValidator<RefrescarRequest>
{
    public RefrescarValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(128);
}

/// <summary>
/// Renueva el token de acceso con el refresh token (rotativo y de un solo uso). Si se presenta un refresh ya usado o inválido no se
/// emite nada. La sesión no se alarga más allá de su vencimiento original.
/// </summary>
public sealed class ManejadorRefrescar(BilleteraDbContext db, EmisorSesion emisor)
{
    public async Task<IResult> EjecutarAsync(RefrescarRequest req, CancellationToken ct)
    {
        var sesion = await emisor.BuscarPorRefreshAsync(req.RefreshToken, ct);
        if (sesion is null) return Results.Problem(title: "Sesión inválida", detail: "Ingresa de nuevo.", statusCode: 401);
        var t = emisor.Rotar(sesion);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new RefrescarResponse(t.Token, t.ExpiraUtc, t.RefreshToken, t.RefreshExpiraUtc));
    }
}

public sealed class RefrescarFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<RefrescarRequest>, RefrescarValidator>();
        s.AddScoped<ManejadorRefrescar>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/auth/refresh", (RefrescarRequest req, ManejadorRefrescar h, CancellationToken ct) => h.EjecutarAsync(req, ct))
            .AllowAnonymous().ValidarCon<RefrescarRequest>().WithTags("Auth");
}
