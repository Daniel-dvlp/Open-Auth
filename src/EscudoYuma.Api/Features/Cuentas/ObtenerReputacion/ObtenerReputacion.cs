using System.Security.Claims;
using EscudoYuma.Api.Shared.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace EscudoYuma.Api.Features.Cuentas.ObtenerReputacion;

/// <param name="Puntaje">Riesgo 0-100 (0 = confiable): el calculado por el motor más el ajuste de analistas.</param>
/// <param name="Reputacion">Misma medida en la convención del modelo (100 = confiable): 100 - Puntaje.</param>
/// <param name="Senales">Señales automáticas vigentes (fan-in, cuenta reciente, patrón mula) y resoluciones de analistas.</param>
public sealed record ReputacionResponse(string IdCuenta, int Puntaje, int Reputacion, IReadOnlyList<string> Senales, DateTime? ActualizadoUtc, bool BloqueoDefinitivo);

public sealed class ManejadorObtenerReputacion(EscudoDbContext db)
{
    public async Task<IResult> EjecutarAsync(string idCuenta, ClaimsPrincipal user, CancellationToken ct)
    {
        // Denegar por defecto: analistas y servicios consultan cualquier cuenta; cualquier otro rol (incluido "cliente" o un rol
        // desconocido) solo la propia (BOLA/IDOR).
        var rol = user.FindFirst("rol")?.Value;
        var privilegiado = rol is Roles.Analista or Roles.Servicio;
        if (!privilegiado && (user.FindFirst("sub")?.Value is not { Length: > 0 } sub || !string.Equals(sub, idCuenta, StringComparison.Ordinal)))
            return Results.Forbid();

        var rep = await db.Reputaciones.AsNoTracking().FirstOrDefaultAsync(r => r.IdCuenta == idCuenta, ct);
        if (rep is not null)
            return Results.Ok(new ReputacionResponse(rep.IdCuenta, rep.Puntaje, 100 - rep.Puntaje,
                rep.SenalesAutomaticas.Concat(rep.Senales).ToList(), rep.ActualizadoUtc, rep.BloqueoDefinitivo));

        // Sin historial: reputación neutra si la cuenta existe como usuario; 404 en otro caso.
        return await db.Usuarios.AnyAsync(u => u.IdCliente == idCuenta, ct)
            ? Results.Ok(new ReputacionResponse(idCuenta, 0, 100, [], null, false))
            : Results.Problem(title: "Cuenta no encontrada", statusCode: 404);
    }
}

public sealed class ObtenerReputacionFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorObtenerReputacion>();

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapGet("/v1/cuentas/{idCuenta}/reputacion", (string idCuenta, ManejadorObtenerReputacion h, ClaimsPrincipal user, CancellationToken ct) =>
                h.EjecutarAsync(idCuenta, user, ct))
            .RequireAuthorization().WithTags("Cuentas");
}
