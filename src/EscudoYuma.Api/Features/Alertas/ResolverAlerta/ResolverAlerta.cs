using System.Security.Claims;
using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Features;
using EscudoYuma.Api.Shared.Observabilidad;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.ReputacionCuentas;
using EscudoYuma.Api.Shared.Seguridad;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace EscudoYuma.Api.Features.Alertas.ResolverAlerta;

public static class Resoluciones
{
    public const string FraudeConfirmado = "fraude_confirmado";
    public const string FalsoPositivo = "falso_positivo";
}

public static class Acciones
{
    public const string BloquearCuenta = "bloquear_cuenta";
    public const string Liberar = "liberar_transaccion";
    public const string MantenerRetencion = "mantener_retencion";
    public const string Monitorear = "monitorear";
    public static readonly string[] Todas = [BloquearCuenta, Liberar, MantenerRetencion, Monitorear];
}

public sealed record ResolverAlertaRequest(string Resolucion, string Accion, string? Comentario);
public sealed record ResolverAlertaResponse(Guid IdAlerta, string Estado, string Resolucion, string Accion, int PuntajeReputacion, bool BloqueoDefinitivo);

public sealed class ResolverAlertaValidator : AbstractValidator<ResolverAlertaRequest>
{
    public ResolverAlertaValidator()
    {
        RuleFor(x => x.Resolucion).Must(r => r is Resoluciones.FraudeConfirmado or Resoluciones.FalsoPositivo)
            .WithMessage("resolucion debe ser fraude_confirmado o falso_positivo.");
        RuleFor(x => x.Accion).Must(a => Acciones.Todas.Contains(a))
            .WithMessage("accion debe ser bloquear_cuenta, liberar_transaccion, mantener_retencion o monitorear.");
        RuleFor(x => x.Comentario).MaximumLength(500);
        // El bloqueo definitivo solo tiene sentido (y solo se permite) cuando el analista confirma el fraude.
        RuleFor(x => x).Must(x => x.Accion != Acciones.BloquearCuenta || x.Resolucion == Resoluciones.FraudeConfirmado)
            .WithName("accion").WithMessage("bloquear_cuenta exige resolucion fraude_confirmado.");
        RuleFor(x => x).Must(x => !(x.Resolucion == Resoluciones.FalsoPositivo && x.Accion == Acciones.MantenerRetencion))
            .WithName("accion").WithMessage("Un falso positivo no puede mantener la retención.");
    }
}

/// <summary>
/// ÚNICO camino del sistema que puede marcar el bloqueo definitivo de una cuenta, y solo con un analista humano autenticado.
/// La resolución retroalimenta la reputación: fraude confirmado la sube, falso positivo la baja.
/// </summary>
public sealed class ManejadorResolverAlerta(EscudoDbContext db, ServicioAuditoria auditoria, TimeProvider reloj, RegistroBloqueos bloqueos)
{
    public const int AjusteFraude = 30;
    public const int AjusteFalsoPositivo = -15;

    public async Task<IResult> EjecutarAsync(Guid idAlerta, ResolverAlertaRequest req, ClaimsPrincipal user, CancellationToken ct)
    {
        var analista = user.FindFirst("sub")?.Value ?? "desconocido";
        var alerta = await db.Alertas.FirstOrDefaultAsync(a => a.Id == idAlerta, ct);
        if (alerta is null) return Results.Problem(title: "Alerta no encontrada", statusCode: 404);
        if (alerta.Estado != EstadosAlerta.Pendiente)
            return Results.Problem(title: "Alerta ya resuelta", detail: "La alerta ya tiene una resolución registrada.", statusCode: 409);

        var ahora = reloj.GetUtcNow().UtcDateTime;
        alerta.Estado = EstadosAlerta.Resuelta;
        alerta.Analista = analista;
        alerta.Resolucion = req.Resolucion;
        alerta.Accion = req.Accion;
        alerta.Comentario = req.Comentario;
        alerta.ResueltaUtc = ahora;

        var rep = await db.Reputaciones.FirstOrDefaultAsync(r => r.IdCuenta == alerta.IdCliente, ct);
        if (rep is null)
        {
            rep = new ReputacionCuenta { IdCuenta = alerta.IdCliente, ActualizadoUtc = ahora };
            db.Reputaciones.Add(rep);
        }

        if (req.Resolucion == Resoluciones.FraudeConfirmado)
        {
            rep.AjustarPuntaje(AjusteFraude, ahora);
            rep.AgregarSenal($"fraude_confirmado:{alerta.IdTransaccion}");
        }
        else
        {
            rep.AjustarPuntaje(AjusteFalsoPositivo, ahora);
            rep.AgregarSenal($"falso_positivo:{alerta.IdTransaccion}");
        }

        if (req.Accion == Acciones.BloquearCuenta) rep.MarcarBloqueoDefinitivo(analista, ahora); // ÚNICA llamada en todo el sistema

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            // Otro analista resolvió la misma alerta entre la lectura y el guardado: no se duplica el ajuste de reputación ni el bloqueo.
            return Results.Problem(title: "Alerta ya resuelta", detail: "La alerta ya tiene una resolución registrada.", statusCode: 409);
        }
        if (rep.BloqueoDefinitivo) bloqueos.Marcar(rep.IdCuenta); // el pipeline de scoring consulta este registro en memoria
        await auditoria.RegistrarAsync("alerta_resuelta", "alertas", new
        {
            id_alerta = alerta.Id, id_transaccion = alerta.IdTransaccion, id_cliente = alerta.IdCliente,
            resolucion = req.Resolucion, accion = req.Accion, bloqueo_definitivo = rep.BloqueoDefinitivo,
        }, actor: analista, ct: ct);
        MetricasYuma.AlertasResueltas.WithLabels(req.Resolucion).Inc();

        return Results.Ok(new ResolverAlertaResponse(alerta.Id, alerta.Estado, req.Resolucion, req.Accion, rep.Puntaje, rep.BloqueoDefinitivo));
    }
}

public sealed class ResolverAlertaFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<ResolverAlertaRequest>, ResolverAlertaValidator>();
        s.AddScoped<ManejadorResolverAlerta>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/alertas/{idAlerta:guid}/resolver", (Guid idAlerta, ResolverAlertaRequest req, ManejadorResolverAlerta h, ClaimsPrincipal user, CancellationToken ct) =>
                h.EjecutarAsync(idAlerta, req, user, ct))
            .RequireAuthorization(Politicas.Analista).ValidarCon<ResolverAlertaRequest>().WithTags("Alertas");
}
