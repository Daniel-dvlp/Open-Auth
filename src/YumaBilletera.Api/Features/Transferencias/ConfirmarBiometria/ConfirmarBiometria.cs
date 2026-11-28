using System.Security.Claims;
using YumaBilletera.Api.Shared.AutenticacionReforzada;
using YumaBilletera.Api.Shared.Contabilidad;
using YumaBilletera.Api.Shared.Operaciones;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Transferencias.ConfirmarBiometria;

/// <param name="ResultadoSimulado">supera | no_supera (biometría facial simulada).</param>
public sealed record ConfirmarBiometriaTransferenciaRequest(Guid IdVerificacion, string ResultadoSimulado);

public sealed class ConfirmarBiometriaTransferenciaValidator : AbstractValidator<ConfirmarBiometriaTransferenciaRequest>
{
    public ConfirmarBiometriaTransferenciaValidator()
    {
        RuleFor(x => x.IdVerificacion).NotEmpty();
        RuleFor(x => x.ResultadoSimulado).Must(r => r is BiometriaSimulada.Supera or BiometriaSimulada.NoSupera)
            .WithMessage("resultado_simulado debe ser supera o no_supera.");
    }
}

/// <summary>
/// Autenticación reforzada de una operación de riesgo medio (decisión "desafiar" del Escudo), según el diagrama "Flujo de autorización":
/// ¿la supera? sí → autorizar · no → crear caso para el analista (la operación queda en revisión y el monto sigue reservado).
/// </summary>
public sealed class ManejadorConfirmarBiometriaTransferencia(
    BilleteraDbContext db, ServicioVerificacion verificaciones, ProcesadorOperacion procesador, ServicioAuditoria auditoria)
{
    public async Task<IResult> EjecutarAsync(string id, ConfirmarBiometriaTransferenciaRequest req, ClaimsPrincipal u, CancellationToken ct)
    {
        var idCliente = u.IdCliente();
        var t = await db.Transferencias.FirstOrDefaultAsync(x => x.IdTransaccion == id && x.IdClienteOrigen == idCliente, ct);
        if (t is null) return Results.NotFound();
        if (t.Estado != EstadosTransferencia.RequiereVerificacion)
            return Results.Problem(title: "La operación no está pendiente de verificación.", statusCode: 409);
        var v = await verificaciones.BuscarAsync(req.IdVerificacion, idCliente, PropositosVerificacion.Transferencia, ct);
        if (v is null || v.Referencia != t.IdTransaccion) return Results.Problem(title: "Verificación no encontrada.", statusCode: 404);

        var (estado, detalle) = await verificaciones.ValidarBiometriaAsync(v, req.ResultadoSimulado, ct);
        if (estado == EstadoIntento.Valido)
        {
            try { await procesador.CompletarAsync(t, ct); }
            catch (ReglaContableException) { await procesador.RechazarAsync(t, MensajesCliente.Rechazada, ct); }
        }
        else procesador.PasarARevisionPorVerificacion(t, estado == EstadoIntento.Expirada ? "Verificación vencida." : detalle);
        t.Version = Guid.NewGuid();

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(title: "La cuenta cambió mientras se confirmaba. Intenta de nuevo.", statusCode: 409);
        }
        MetricasBilletera.Operaciones.WithLabels(t.TipoOperacion, t.Estado).Inc();
        await auditoria.RegistrarAsync("operacion_biometria_" + (estado == EstadoIntento.Valido ? "superada" : "no_superada"), "operaciones/" + t.IdTransaccion,
            new { id_transaccion = t.IdTransaccion, estado_final = t.Estado, resultado = estado.ToString() }, ct: ct);
        return Results.Ok(OperacionResponse.De(t));
    }
}

public sealed class ConfirmarBiometriaTransferenciaFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<ConfirmarBiometriaTransferenciaRequest>, ConfirmarBiometriaTransferenciaValidator>();
        s.AddScoped<ManejadorConfirmarBiometriaTransferencia>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/transferencias/{id}/biometria", (string id, ConfirmarBiometriaTransferenciaRequest req, ClaimsPrincipal u,
                ManejadorConfirmarBiometriaTransferencia h, CancellationToken ct) => h.EjecutarAsync(id, req, u, ct))
            .RequireAuthorization(Politicas.Cliente).ValidarCon<ConfirmarBiometriaTransferenciaRequest>().WithTags("Transferencias");
}
