using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Expediente;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Seguridad;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace EscudoYuma.Api.Features.Alertas.ReportarDesafioFallido;

// ---------- Contrato ----------
/// <summary>La billetera avisa que el cliente no superó la autenticación reforzada de una operación "desafiar".</summary>
public sealed record ReportarDesafioFallidoRequest(
    string IdTransaccion, string IdCliente, long MontoCop, string Metodo, string? Detalle, double? Puntaje, IReadOnlyList<string>? Razones);

public sealed record ReportarDesafioFallidoResponse(Guid IdAlerta, bool Creada);

public sealed class ReportarDesafioFallidoValidator : AbstractValidator<ReportarDesafioFallidoRequest>
{
    public ReportarDesafioFallidoValidator()
    {
        RuleFor(x => x.IdTransaccion).NotEmpty().MaximumLength(32);
        RuleFor(x => x.IdCliente).NotEmpty().MaximumLength(32);
        RuleFor(x => x.MontoCop).GreaterThan(0);
        RuleFor(x => x.Metodo).Must(m => m is "biometria" or "otp").WithMessage("metodo debe ser biometria u otp.");
        RuleFor(x => x.Detalle).MaximumLength(200);
        RuleFor(x => x.Puntaje).InclusiveBetween(0, 1).When(x => x.Puntaje is not null);
        RuleFor(x => x.Razones).Must(r => r is null || r.Count <= 10);
        RuleForEach(x => x.Razones).MaximumLength(300);
    }
}

// ---------- Caso de uso ----------
/// <summary>
/// Diagrama "Flujo de autorización": autenticación reforzada → ¿la supera? no → "Crear caso con explicación: el analista confirma con el
/// cliente". Crea la alerta pendiente (decisión retener) con las razones originales del Escudo más el motivo. Idempotente por id_transaccion.
/// Solo rol servicio. No bloquea ninguna cuenta (eso solo lo decide un analista).
/// </summary>
public sealed class ManejadorReportarDesafioFallido(EscudoDbContext db, ServicioAuditoria auditoria, TimeProvider reloj, RegistroExpediente expediente,
    ColaNarrativas narrativas, ContextoAuditoria contexto)
{
    public async Task<IResult> EjecutarAsync(ReportarDesafioFallidoRequest req, CancellationToken ct)
    {
        var existente = await db.Alertas.AsNoTracking().FirstOrDefaultAsync(a => a.IdTransaccion == req.IdTransaccion, ct);
        if (existente is not null) return Results.Ok(new ReportarDesafioFallidoResponse(existente.Id, false));

        var motivo = req.Metodo == "biometria" ? "Autenticación reforzada (biometría facial) no superada" : "Autenticación reforzada (OTP) no superada";
        var razones = new List<string> { string.IsNullOrWhiteSpace(req.Detalle) ? motivo : $"{motivo}: {req.Detalle}" };
        razones.AddRange(req.Razones ?? []);
        var alerta = new Alerta
        {
            IdTransaccion = req.IdTransaccion, IdCliente = req.IdCliente, MontoCop = req.MontoCop, Puntaje = req.Puntaje ?? 0,
            Decision = "retener", Razones = razones, CreadaUtc = reloj.GetUtcNow().UtcDateTime,
        };
        db.Alertas.Add(alerta);
        await db.SaveChangesAsync(ct);
        var eslabon = await auditoria.RegistrarAsync("alerta_creada", "alertas", new { id_alerta = alerta.Id, id_transaccion = req.IdTransaccion, decision = "retener", origen = "desafio_fallido", metodo = req.Metodo }, ct: ct);
        expediente.Registrar(TiposHecho.DesafioFallido, FuentesHecho.Billetera, req.IdCliente, req.IdTransaccion,
            new { metodo = req.Metodo, detalle = req.Detalle, monto_cop = req.MontoCop }, ["NORMA-AUTENTICACION-REFORZADA"], contexto.IdCorrelacion, eslabon, alerta.CreadaUtc);
        expediente.Registrar(TiposHecho.Alerta, FuentesHecho.Escudo, req.IdCliente, req.IdTransaccion,
            new { id_alerta = alerta.Id, decision = "retener", monto_cop = req.MontoCop }, null, contexto.IdCorrelacion, eslabon, alerta.CreadaUtc);
        narrativas.Encolar(alerta.Id);
        return Results.Created($"/v1/alertas/{alerta.Id}", new ReportarDesafioFallidoResponse(alerta.Id, true));
    }
}

public sealed class ReportarDesafioFallidoFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<ReportarDesafioFallidoRequest>, ReportarDesafioFallidoValidator>();
        s.AddScoped<ManejadorReportarDesafioFallido>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/alertas/desafio-fallido", (ReportarDesafioFallidoRequest req, ManejadorReportarDesafioFallido h, CancellationToken ct) => h.EjecutarAsync(req, ct))
            .RequireAuthorization(Politicas.Servicio).ValidarCon<ReportarDesafioFallidoRequest>().WithTags("Alertas");
}
