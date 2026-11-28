using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Features.Auth.Login;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Expediente;
using EscudoYuma.Api.Shared.Observabilidad;
using EscudoYuma.Api.Shared.Seguridad;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace EscudoYuma.Api.Features.Auth.EvaluarIngreso;

// ---------- Contrato ----------
/// <summary>La billetera ya validó llave y clave; pide el score de ingreso con su contexto (dispositivos que tiene registrados).</summary>
public sealed record EvaluarIngresoRequest(string IdCliente, string IdDispositivo, string DepartamentoIp, IReadOnlyList<string>? DispositivosConocidos);

/// <param name="Resultado">permitir | otp | bloqueo_temporal (mismos umbrales del login del Escudo).</param>
public sealed record EvaluarIngresoResponse(string Resultado, double ScoreIngreso, IReadOnlyList<string> Razones);

public sealed class EvaluarIngresoValidator : AbstractValidator<EvaluarIngresoRequest>
{
    public EvaluarIngresoValidator()
    {
        RuleFor(x => x.IdCliente).NotEmpty().MaximumLength(32);
        RuleFor(x => x.IdDispositivo).NotEmpty().MaximumLength(64);
        RuleFor(x => x.DepartamentoIp).NotEmpty().MaximumLength(64);
        RuleFor(x => x.DispositivosConocidos).Must(d => d is null || d.Count <= 50).WithMessage("Máximo 50 dispositivos conocidos.");
        RuleForEach(x => x.DispositivosConocidos).NotEmpty().MaximumLength(64);
    }
}

// ---------- Caso de uso ----------
/// <summary>
/// Score de ingreso para un canal externo (la billetera). Mismo cálculo y umbrales que <c>/v1/auth/login</c> (<see cref="IScoreIngreso"/>,
/// port de score_login.py) sobre el estado en línea, y el score queda registrado para escalar la política de las transacciones de la sesión.
/// No procesa eventos: la billetera envía el inicio de sesión (exitoso/fallido) por <c>/v1/eventos</c>, una sola fuente por evento.
/// Solo rol servicio.
/// </summary>
public sealed class ManejadorEvaluarIngreso(IEstadoEnLinea estado, IScoreIngreso score, IOptions<LoginOptions> opciones, ServicioAuditoria auditoria, TimeProvider reloj,
    RegistroExpediente expediente, ContextoAuditoria ctxAuditoria)
{
    public async Task<EvaluarIngresoResponse> EjecutarAsync(EvaluarIngresoRequest req, CancellationToken ct)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var contexto = estado.ConsultarLogin(req.IdCliente, Tiempo.AEpochColombia(ahora), req.IdDispositivo, req.DepartamentoIp, req.DispositivosConocidos ?? []);
        var res = score.Calcular(contexto);
        var opc = opciones.Value;
        var resultado = res.Score >= opc.UmbralBloqueo ? "bloqueo_temporal" : res.Score >= opc.UmbralOtp ? "otp" : "permitir";
        estado.RegistrarScoreIngreso(req.IdCliente, res.Score, ahora);
        MetricasYuma.Logins.WithLabels("billetera_" + resultado).Inc();
        var eslabon = await auditoria.RegistrarAsync("ingreso_evaluado", "auth/evaluar-ingreso",
            new { id_cliente = req.IdCliente, resultado, score_ingreso = res.Score, razones = res.Razones }, ct: ct);
        expediente.Registrar(TiposHecho.Ingreso, FuentesHecho.Billetera, req.IdCliente, null, new
        {
            resultado, score_ingreso = Math.Round(res.Score, 4), razones = res.Razones, id_dispositivo = req.IdDispositivo, departamento_ip = req.DepartamentoIp,
        }, null, ctxAuditoria.IdCorrelacion, eslabon, ahora);
        return new EvaluarIngresoResponse(resultado, res.Score, res.Razones);
    }
}

public sealed class EvaluarIngresoFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<EvaluarIngresoRequest>, EvaluarIngresoValidator>();
        s.AddScoped<ManejadorEvaluarIngreso>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/auth/evaluar-ingreso", async (EvaluarIngresoRequest req, ManejadorEvaluarIngreso h, CancellationToken ct) => Results.Ok(await h.EjecutarAsync(req, ct)))
            .RequireAuthorization(Politicas.Servicio).ValidarCon<EvaluarIngresoRequest>().WithTags("Auth");
}
