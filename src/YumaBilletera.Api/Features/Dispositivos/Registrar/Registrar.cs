using System.Security.Claims;
using YumaBilletera.Api.Shared.Escudo;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Dispositivos.Registrar;

/// <param name="SistemaOperativo">Android | iOS (valores de eventos_app.csv).</param>
public sealed record RegistrarDispositivoRequest(string IdDispositivo, string SistemaOperativo);
public sealed record DispositivoDto(string IdDispositivo, string? SistemaOperativo, DateTime RegistradoUtc);
public sealed record RegistrarDispositivoResponse(DispositivoDto Dispositivo, bool NuevoRegistro);

public sealed class RegistrarDispositivoValidator : AbstractValidator<RegistrarDispositivoRequest>
{
    public RegistrarDispositivoValidator()
    {
        RuleFor(x => x.IdDispositivo).NotEmpty().MaximumLength(64);
        RuleFor(x => x.SistemaOperativo).Must(s => s is "Android" or "iOS").WithMessage("sistema_operativo debe ser Android o iOS.");
    }
}

/// <summary>
/// Registra un dispositivo del cliente (máximo 20, igual que el Escudo). El registro se avisa al Escudo (evento registro_dispositivo):
/// un dispositivo registrado hace menos de 24 h es señal de riesgo (dispositivo_nuevo) en las siguientes operaciones.
/// </summary>
public sealed class ManejadorRegistrarDispositivo(BilleteraDbContext db, PublicadorEventos publicador, ServicioAuditoria auditoria, TimeProvider reloj)
{
    public const int MaxPorCliente = 20;

    public async Task<IResult> EjecutarAsync(RegistrarDispositivoRequest req, ClaimsPrincipal u, CancellationToken ct)
    {
        var idCliente = u.IdCliente();
        var existente = await db.Dispositivos.AsNoTracking().FirstOrDefaultAsync(d => d.IdCliente == idCliente && d.IdDispositivo == req.IdDispositivo, ct);
        if (existente is not null)
            return Results.Ok(new RegistrarDispositivoResponse(new DispositivoDto(existente.IdDispositivo, existente.SistemaOperativo, existente.RegistradoUtc), false));
        if (await db.Dispositivos.CountAsync(d => d.IdCliente == idCliente, ct) >= MaxPorCliente)
            return Results.Problem(title: $"Alcanzaste el máximo de {MaxPorCliente} dispositivos registrados.", statusCode: 422);

        var sesion = await db.Sesiones.AsNoTracking().FirstOrDefaultAsync(s => s.IdSesion == u.IdSesion(), ct);
        var d = new Dispositivo { IdCliente = idCliente, IdDispositivo = req.IdDispositivo, SistemaOperativo = req.SistemaOperativo, RegistradoUtc = reloj.GetUtcNow().UtcDateTime };
        db.Dispositivos.Add(d);
        publicador.Encolar(idCliente, TiposEventoApp.RegistroDispositivo, d.IdDispositivo, d.SistemaOperativo, sesion?.DepartamentoIp, exitoso: true);
        await db.SaveChangesAsync(ct);
        await auditoria.RegistrarAsync("dispositivo_registrado", "dispositivos", new { id_dispositivo = d.IdDispositivo, sistema_operativo = d.SistemaOperativo }, ct: ct);
        return Results.Created("/v1/dispositivos", new RegistrarDispositivoResponse(new DispositivoDto(d.IdDispositivo, d.SistemaOperativo, d.RegistradoUtc), true));
    }
}

public sealed class RegistrarDispositivoFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<RegistrarDispositivoRequest>, RegistrarDispositivoValidator>();
        s.AddScoped<ManejadorRegistrarDispositivo>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/dispositivos", (RegistrarDispositivoRequest req, ClaimsPrincipal u, ManejadorRegistrarDispositivo h, CancellationToken ct) =>
                h.EjecutarAsync(req, u, ct))
            .RequireAuthorization(Politicas.Cliente).ValidarCon<RegistrarDispositivoRequest>().WithTags("Dispositivos");
}
