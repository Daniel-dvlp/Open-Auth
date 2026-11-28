using System.Security.Claims;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Cuentas.ListarMovimientos;

/// <param name="FechaColombia">Hora local de Colombia (AAAA-MM-DD HH:MM:SS).</param>
/// <param name="Contraparte">Cuenta del otro lado del asiento (otro cliente Yuma, PUENTE-&lt;tipo&gt; o APERTURA).</param>
public sealed record MovimientoDto(
    long Id, DateTime FechaUtc, string FechaColombia, string Tipo, string Concepto, long MontoCop, long SaldoDespuesCop, string? IdTransaccion, string Contraparte);

public sealed record MovimientosResponse(IReadOnlyList<MovimientoDto> Movimientos, long? SiguienteCursor);

/// <summary>Historial de la cuenta propia, del más reciente al más antiguo, paginado por cursor (id del último movimiento recibido).</summary>
public sealed class ManejadorListarMovimientos(BilleteraDbContext db)
{
    public const int LimitePorDefecto = 20, LimiteMaximo = 100;

    public async Task<IResult> EjecutarAsync(ClaimsPrincipal usuario, long? cursor, int? limite, CancellationToken ct)
    {
        var n = limite ?? LimitePorDefecto;
        if (n is < 1 or > LimiteMaximo)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["limite"] = [$"limite debe estar entre 1 y {LimiteMaximo}."] });
        var id = usuario.IdCliente();
        var q = db.Movimientos.AsNoTracking().Where(m => m.IdCuenta == id);
        if (cursor is not null) q = q.Where(m => m.Id < cursor);
        var lista = await q.OrderByDescending(m => m.Id).Take(n + 1).ToListAsync(ct);
        var pagina = lista.Take(n).Select(m => new MovimientoDto(m.Id, m.FechaUtc, HoraColombia.Formatear(m.FechaUtc), m.Tipo, m.Concepto,
            m.MontoCop, m.SaldoDespuesCop, m.IdTransaccion, m.Contraparte)).ToList();
        return Results.Ok(new MovimientosResponse(pagina, lista.Count > n ? pagina[^1].Id : null));
    }
}

public sealed class ListarMovimientosFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorListarMovimientos>();

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapGet("/v1/cuentas/me/movimientos", (ClaimsPrincipal u, long? cursor, int? limite, ManejadorListarMovimientos h, CancellationToken ct) =>
                h.EjecutarAsync(u, cursor, limite, ct))
            .RequireAuthorization(Politicas.Cliente).WithTags("Cuentas");
}
