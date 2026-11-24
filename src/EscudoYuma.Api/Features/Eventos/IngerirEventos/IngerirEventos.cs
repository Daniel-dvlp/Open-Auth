using System.Globalization;
using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Features;
using EscudoYuma.Api.Shared.Seguridad;
using FluentValidation;

namespace EscudoYuma.Api.Features.Eventos.IngerirEventos;

/// <summary>Lote de eventos de la app con el esquema de eventos_app.csv (snake_case): <c>{"eventos":[{...}]}</c>.</summary>
public sealed record IngerirEventosRequest(IReadOnlyList<EventoApp> Eventos);
public sealed record IngerirEventosResponse(int Procesados);

public sealed class IngerirEventosValidator : AbstractValidator<IngerirEventosRequest>
{
    public const int MaxPorLote = 1000;

    public IngerirEventosValidator(TimeProvider reloj)
    {
        RuleFor(x => x.Eventos).NotNull().Must(e => e is { Count: > 0 and <= MaxPorLote })
            .WithMessage($"Se esperan entre 1 y {MaxPorLote} eventos por solicitud.");
        RuleForEach(x => x.Eventos).NotNull().WithMessage("Los eventos no pueden ser nulos.").ChildRules(e =>
        {
            e.RuleFor(x => x.IdEvento).NotEmpty().MaximumLength(64);
            e.RuleFor(x => x.IdCliente).NotEmpty().MaximumLength(32);
            e.RuleFor(x => x.FechaHora).Must(f => f is not null && DateTime.TryParseExact(f, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                .WithMessage("fecha_hora debe tener el formato AAAA-MM-DD HH:MM:SS.");
            // Fecha acotada: un evento con fecha futura quedaría "vigente" para siempre (p. ej. un cambio de SIM) y envenenaría el score del cliente.
            e.RuleFor(x => x.FechaHora).Must(f => RangoFecha.EsValida(f, reloj.GetUtcNow().UtcDateTime))
                .When(x => x.FechaHora is not null && DateTime.TryParseExact(x.FechaHora, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                .WithMessage("fecha_hora fuera de rango (no antes de 2020 ni más de 1 día en el futuro).");
            e.RuleFor(x => x.SistemaOperativo).MaximumLength(16);
            e.RuleFor(x => x.TipoEvento).Must(t => TiposEvento.Todos.Contains(t)).WithMessage("tipo_evento no reconocido.");
            e.RuleFor(x => x.Resultado).Must(r => r is null or "exitoso" or "fallido").WithMessage("resultado debe ser exitoso o fallido.");
            e.RuleFor(x => x.IdDispositivo).MaximumLength(64);
            e.RuleFor(x => x.DepartamentoIp).MaximumLength(64);
        }).When(x => x.Eventos is not null);
    }
}

/// <summary>
/// Ingesta de eventos de la app (inicio de sesión, registro de dispositivo, cambio de clave, cambio de SIM) al estado en línea.
/// Interno: solo rol servicio. Los eventos deben llegar en orden cronológico respecto de las transacciones (el estado no se reordena);
/// dentro de un lote se ordenan por (fecha_hora, id_evento).
/// </summary>
public sealed class ManejadorIngerirEventos(IEstadoEnLinea estado, ServicioAuditoria auditoria)
{
    public async Task<IngerirEventosResponse> EjecutarAsync(IngerirEventosRequest req, CancellationToken ct)
    {
        foreach (var e in req.Eventos.OrderBy(e => e.FechaHora, StringComparer.Ordinal).ThenBy(e => e.IdEvento, StringComparer.Ordinal))
            estado.ProcesarEvento(e);
        // Trazabilidad: quién (actor = sub del token de servicio) alteró el estado en línea y cuántos eventos; sin datos personales.
        await auditoria.RegistrarAsync("eventos_ingeridos", "eventos", new { cantidad = req.Eventos.Count, clientes = req.Eventos.Select(e => e.IdCliente).Distinct().Count() }, ct: ct);
        return new IngerirEventosResponse(req.Eventos.Count);
    }
}

public sealed class IngerirEventosFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<IngerirEventosRequest>, IngerirEventosValidator>();
        s.AddScoped<ManejadorIngerirEventos>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/eventos", async (IngerirEventosRequest req, ManejadorIngerirEventos h, CancellationToken ct) => Results.Ok(await h.EjecutarAsync(req, ct)))
            .RequireAuthorization(Politicas.Servicio).ValidarCon<IngerirEventosRequest>().WithTags("Eventos");
}
