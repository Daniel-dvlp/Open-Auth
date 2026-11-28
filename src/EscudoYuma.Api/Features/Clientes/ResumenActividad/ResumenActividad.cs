using System.Text.Json;
using EscudoYuma.Api.Shared.Expediente;
using EscudoYuma.Api.Shared.Seguridad;

namespace EscudoYuma.Api.Features.Clientes.ResumenActividad;

public sealed record ResumenActividadResponse(
    string IdCliente, int Dias, int Hechos, string Origen, string Modelo, string? MotivoRespaldo, double LatenciaMs, ContenidoNarrativa Contenido);

/// <summary>
/// Resumen narrado de la actividad de un cliente (hechos del expediente de los últimos N días, 1 a 90). Síncrono: con el LLM en CPU puede
/// tardar varios segundos; si no responde a tiempo o su salida no pasa la validación, se entrega la versión por plantilla. Solo analista.
/// </summary>
public sealed class ManejadorResumenActividad(ConstructorExpediente constructor, ServicioNarrativas narrativas, TimeProvider reloj)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<IResult> EjecutarAsync(string idCliente, int? dias, CancellationToken ct)
    {
        var d = dias ?? 30;
        if (d is < 1 or > 90 || string.IsNullOrWhiteSpace(idCliente) || idCliente.Length > 32)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["dias"] = ["dias debe estar entre 1 y 90."] });
        var hechos = await constructor.PorClienteAsync(idCliente, reloj.GetUtcNow().UtcDateTime.AddDays(-d), ct);
        var normas = hechos.SelectMany(h => h.Normas).Where(CatalogoNormas.Todas.ContainsKey).ToHashSet();
        var material = new MaterialNarrable("actividad_cliente", JsonSerializer.Serialize(new
        {
            id_cliente = idCliente, dias = d, hechos,
            normas_aplicadas = normas.Select(c => CatalogoNormas.Todas[c]),
        }, Json), normas);
        var plantilla = NarradorPlantilla.DeActividad(idCliente, hechos, d);
        var (contenido, origen, modelo, motivo, ms) = await narrativas.NarrarConRespaldoAsync(material, () => plantilla, ct, plantilla.LineaDeTiempo);
        return Results.Ok(new ResumenActividadResponse(idCliente, d, hechos.Count, origen, modelo, motivo, Math.Round(ms, 1), contenido));
    }
}

public sealed class ResumenActividadFeature : IFeature
{
    public void AddServices(IServiceCollection s) => s.AddScoped<ManejadorResumenActividad>();

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapGet("/v1/clientes/{idCliente}/resumen", (string idCliente, int? dias, ManejadorResumenActividad h, CancellationToken ct) => h.EjecutarAsync(idCliente, dias, ct))
            .RequireAuthorization(Politicas.Analista).WithTags("Clientes");
}
