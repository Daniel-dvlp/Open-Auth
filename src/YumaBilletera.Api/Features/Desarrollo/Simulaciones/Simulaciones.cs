using YumaBilletera.Api.Shared.AutenticacionReforzada;
using YumaBilletera.Api.Shared.Escudo;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Desarrollo.Simulaciones;

/// <summary>Cambio de SIM reportado por el operador (simulado). En producción esta señal llega del operador de telefonía.</summary>
public sealed record SimularCambioSimRequest(string IdCliente);

public sealed class SimularCambioSimValidator : AbstractValidator<SimularCambioSimRequest>
{
    public SimularCambioSimValidator() => RuleFor(x => x.IdCliente).NotEmpty().MaximumLength(32);
}

/// <summary>
/// Herramientas SOLO de Development (no se mapean en otro ambiente): leer el OTP "enviado" por SMS y simular el reporte de cambio de
/// SIM del operador (puerto IOperadorSim simulado del plan), para poder probar en vivo las modalidades del caso.
/// </summary>
public sealed class ManejadorSimulaciones(BilleteraDbContext db, PublicadorEventos publicador, ServicioAuditoria auditoria, TimeProvider reloj)
{
    public IResult LeerOtp(Guid id, BuzonOtpDesarrollo buzon) =>
        buzon.Leer(id) is { } codigo ? Results.Ok(new { id_verificacion = id, codigo }) : Results.NotFound();

    public async Task<IResult> CambioSimAsync(SimularCambioSimRequest req, CancellationToken ct)
    {
        if (!await db.Clientes.AnyAsync(c => c.IdCliente == req.IdCliente, ct)) return Results.NotFound();
        var ahora = reloj.GetUtcNow().UtcDateTime;
        db.EventosSeguridad.Add(new EventoSeguridad { IdCliente = req.IdCliente, Tipo = TiposEventoSeguridad.CambioSim, FechaUtc = ahora });
        publicador.Encolar(req.IdCliente, TiposEventoApp.CambioSim, null, null, null, exitoso: true);
        await db.SaveChangesAsync(ct);
        await auditoria.RegistrarAsync("simulacion_cambio_sim", "dev/cambio-sim", new { id_cliente = req.IdCliente }, actor: "simulador", ct: ct);
        return Results.Accepted();
    }
}

public sealed class SimulacionesFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<SimularCambioSimRequest>, SimularCambioSimValidator>();
        s.AddScoped<ManejadorSimulaciones>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        if (!r.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment()) return;
        r.MapGet("/v1/dev/otp/{id:guid}", (Guid id, ManejadorSimulaciones h, BuzonOtpDesarrollo buzon) => h.LeerOtp(id, buzon))
            .AllowAnonymous().WithTags("Desarrollo");
        r.MapPost("/v1/dev/cambio-sim", (SimularCambioSimRequest req, ManejadorSimulaciones h, CancellationToken ct) => h.CambioSimAsync(req, ct))
            .AllowAnonymous().ValidarCon<SimularCambioSimRequest>().WithTags("Desarrollo");
    }
}
