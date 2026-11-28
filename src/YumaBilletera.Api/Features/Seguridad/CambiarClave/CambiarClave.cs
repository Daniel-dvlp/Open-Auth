using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using YumaBilletera.Api.Shared.Escudo;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Seguridad.CambiarClave;

public sealed record CambiarClaveRequest(string ClaveActual, string ClaveNueva);

public sealed class CambiarClaveValidator : AbstractValidator<CambiarClaveRequest>
{
    public CambiarClaveValidator()
    {
        RuleFor(x => x.ClaveActual).NotEmpty().MaximumLength(128);
        RuleFor(x => x.ClaveNueva).NotEmpty().MinimumLength(8).MaximumLength(128).WithMessage("La clave nueva debe tener entre 8 y 128 caracteres.");
        RuleFor(x => x).Must(x => x.ClaveActual != x.ClaveNueva).WithName("clave_nueva").WithMessage("La clave nueva debe ser distinta de la actual.");
    }
}

/// <summary>
/// Cambio de clave. Es una señal de riesgo (toma de cuenta: el delincuente suele cambiar la clave antes de vaciar la cuenta): queda como
/// cambio_clave para las operaciones de las 24 h siguientes, se avisa al Escudo y se cierran las demás sesiones abiertas del cliente.
/// </summary>
public sealed class ManejadorCambiarClave(BilleteraDbContext db, IPasswordHasher<Cliente> hasher, PublicadorEventos publicador,
    ServicioAuditoria auditoria, TimeProvider reloj)
{
    public async Task<IResult> EjecutarAsync(CambiarClaveRequest req, ClaimsPrincipal u, CancellationToken ct)
    {
        var idCliente = u.IdCliente();
        var idSesion = u.IdSesion();
        var cliente = await db.Clientes.FirstAsync(c => c.IdCliente == idCliente, ct);
        if (hasher.VerifyHashedPassword(cliente, cliente.HashClave, req.ClaveActual) == PasswordVerificationResult.Failed)
        {
            await auditoria.RegistrarAsync("cambio_clave_fallido", "seguridad/clave", ct: ct);
            return Results.Problem(title: "La clave actual no es correcta.", statusCode: 401);
        }
        var ahora = reloj.GetUtcNow().UtcDateTime;
        cliente.HashClave = hasher.HashPassword(cliente, req.ClaveNueva);
        db.EventosSeguridad.Add(new EventoSeguridad { IdCliente = idCliente, Tipo = TiposEventoSeguridad.CambioClave, FechaUtc = ahora });
        var sesion = await db.Sesiones.AsNoTracking().FirstOrDefaultAsync(s => s.IdSesion == idSesion, ct);
        publicador.Encolar(idCliente, TiposEventoApp.CambioClave, sesion?.IdDispositivo, null, sesion?.DepartamentoIp, exitoso: true);
        foreach (var otra in await db.Sesiones.Where(s => s.IdCliente == idCliente && s.IdSesion != idSesion && s.Estado != EstadosSesion.Cerrada).ToListAsync(ct))
            otra.Estado = EstadosSesion.Cerrada;
        await db.SaveChangesAsync(ct);
        await auditoria.RegistrarAsync("cambio_clave", "seguridad/clave", ct: ct);
        return Results.NoContent();
    }
}

public sealed class CambiarClaveFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<CambiarClaveRequest>, CambiarClaveValidator>();
        s.AddScoped<ManejadorCambiarClave>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/seguridad/clave", (CambiarClaveRequest req, ClaimsPrincipal u, ManejadorCambiarClave h, CancellationToken ct) => h.EjecutarAsync(req, u, ct))
            .RequireAuthorization(Politicas.Cliente).ValidarCon<CambiarClaveRequest>().WithTags("Seguridad");
}
