using System.Security.Claims;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Cuentas.ConsultarSaldo;

/// <param name="ReservadoCop">Dinero retenido por operaciones en evaluación, verificación o revisión (no ha salido de la cuenta).</param>
public sealed record SaldoResponse(
    string IdCuenta, string Llave, string TipoCliente, string Departamento, string Estado, long SaldoCop, long ReservadoCop, long DisponibleCop, string Moneda);

/// <summary>Saldo y estado de la cuenta del cliente autenticado (solo la propia).</summary>
public sealed class ManejadorConsultarSaldo(BilleteraDbContext db)
{
    public async Task<IResult> EjecutarAsync(ClaimsPrincipal usuario, CancellationToken ct)
    {
        var id = usuario.IdCliente();
        var cliente = await db.Clientes.AsNoTracking().FirstOrDefaultAsync(c => c.IdCliente == id, ct);
        var cuenta = await db.Cuentas.AsNoTracking().FirstOrDefaultAsync(c => c.IdCuenta == id, ct);
        if (cliente is null || cuenta is null) return Results.NotFound();
        return Results.Ok(new SaldoResponse(cuenta.IdCuenta, cliente.Llave, cliente.TipoCliente, cliente.Departamento, cuenta.Estado,
            cuenta.SaldoCop, cuenta.ReservadoCop, cuenta.DisponibleCop, "COP"));
    }
}

public sealed class ConsultarSaldoFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorConsultarSaldo>();

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapGet("/v1/cuentas/me", (ClaimsPrincipal u, ManejadorConsultarSaldo h, CancellationToken ct) => h.EjecutarAsync(u, ct))
            .RequireAuthorization(Politicas.Cliente).WithTags("Cuentas");
}
