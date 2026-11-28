using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Seguridad;
using Microsoft.EntityFrameworkCore;

namespace EscudoYuma.Api.Features.Alertas.ListarCola;

public sealed record AlertaItem(
    Guid Id, string IdTransaccion, string IdCliente, long MontoCop, double Puntaje, double Prioridad,
    string Decision, IReadOnlyList<string> Razones, string Estado, DateTime CreadaUtc);

public sealed record ColaResponse(int Total, IReadOnlyList<AlertaItem> Alertas);

/// <summary>Cola de trabajo del analista: ordenada por puntaje x monto (mayor primero).</summary>
public sealed class ManejadorListarCola(EscudoDbContext db)
{
    public async Task<ColaResponse> EjecutarAsync(string estado, int limite, CancellationToken ct)
    {
        var consulta = db.Alertas.AsNoTracking().Where(a => a.Estado == estado);
        var total = await consulta.CountAsync(ct);
        var filas = await consulta.OrderByDescending(a => a.Puntaje * a.MontoCop).ThenBy(a => a.CreadaUtc)
            .Take(limite).ToListAsync(ct);
        return new ColaResponse(total, filas.Select(a => new AlertaItem(a.Id, a.IdTransaccion, a.IdCliente, a.MontoCop, a.Puntaje,
            a.Puntaje * a.MontoCop, a.Decision, a.Razones, a.Estado, a.CreadaUtc)).ToList());
    }
}

public sealed class ListarColaFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorListarCola>();

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapGet("/v1/alertas", async (ManejadorListarCola h, CancellationToken ct, string? estado, int? limite) =>
            {
                if (estado is not (null or EstadosAlerta.Pendiente or EstadosAlerta.Resuelta))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["estado"] = ["Use pendiente o resuelta."] });
                return Results.Ok(await h.EjecutarAsync(estado ?? EstadosAlerta.Pendiente, Math.Clamp(limite ?? 50, 1, 200), ct));
            })
            .RequireAuthorization(Politicas.Analista).WithTags("Alertas");
}
