using System.Security.Claims;
using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Seguridad;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace EscudoYuma.Api.Features.Auth.RegistrarDispositivo;

public sealed record RegistrarDispositivoRequest(string IdDispositivo, string? SistemaOperativo);
public sealed record RegistrarDispositivoResponse(string IdDispositivo, bool NuevoRegistro, DateTime RegistradoUtc);

public sealed class RegistrarDispositivoValidator : AbstractValidator<RegistrarDispositivoRequest>
{
    public RegistrarDispositivoValidator()
    {
        RuleFor(x => x.IdDispositivo).NotEmpty().MaximumLength(64).Matches("^[A-Za-z0-9._:-]+$").WithMessage("id_dispositivo solo admite letras, dígitos y . _ : -");
        RuleFor(x => x.SistemaOperativo).Must(so => so is null or "Android" or "iOS").WithMessage("sistema_operativo debe ser Android o iOS.");
    }
}

/// <summary>Registra un dispositivo como conocido para el cliente autenticado (sesión con login permitido).</summary>
public sealed class ManejadorRegistrarDispositivo(EscudoDbContext db, ServicioAuditoria auditoria, TimeProvider reloj, IEstadoEnLinea estado)
{
    public const int MaxDispositivosPorCliente = 20;

    public async Task<IResult> EjecutarAsync(RegistrarDispositivoRequest req, ClaimsPrincipal user, CancellationToken ct)
    {
        var idCliente = user.FindFirst("sub")?.Value;
        var usuario = await db.Usuarios.Include(u => u.Dispositivos).FirstOrDefaultAsync(u => u.IdCliente == idCliente, ct);
        if (usuario is null) return Results.Problem(title: "Usuario no encontrado", statusCode: 404);

        var existente = usuario.Dispositivos.FirstOrDefault(d => d.IdDispositivo == req.IdDispositivo);
        if (existente is not null)
            return Results.Ok(new RegistrarDispositivoResponse(existente.IdDispositivo, false, existente.RegistradoUtc));

        // Acota el crecimiento de la tabla (y el abuso de "dispositivo conocido" para rebajar el score de ingreso).
        if (usuario.Dispositivos.Count >= MaxDispositivosPorCliente)
            return Results.Problem(title: "Demasiados dispositivos", detail: $"Se admiten hasta {MaxDispositivosPorCliente} dispositivos por cliente.", statusCode: 409);

        var nuevo = new DispositivoConocido
        {
            UsuarioId = usuario.Id, IdDispositivo = req.IdDispositivo, SistemaOperativo = req.SistemaOperativo,
            RegistradoUtc = reloj.GetUtcNow().UtcDateTime,
        };
        db.Dispositivos.Add(nuevo);
        await db.SaveChangesAsync(ct);
        // El registro entra al estado en línea (atributo min_desde_registro_dispositivo y señal del score de ingreso).
        estado.ProcesarEvento(new EventoApp("REG-" + Guid.NewGuid().ToString("N")[..12], Tiempo.AFechaHora(Tiempo.AEpochColombia(nuevo.RegistradoUtc)),
            usuario.IdCliente, TiposEvento.RegistroDispositivo, req.IdDispositivo, req.SistemaOperativo, null, "exitoso"));
        await auditoria.RegistrarAsync("dispositivo_registrado", "auth/dispositivos",
            new { id_cliente = usuario.IdCliente, id_dispositivo = req.IdDispositivo }, ct: ct);
        return Results.Created($"/v1/auth/dispositivos/{req.IdDispositivo}", new RegistrarDispositivoResponse(nuevo.IdDispositivo, true, nuevo.RegistradoUtc));
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
        r.MapPost("/v1/auth/dispositivos", (RegistrarDispositivoRequest req, ManejadorRegistrarDispositivo h, ClaimsPrincipal user, CancellationToken ct) =>
                h.EjecutarAsync(req, user, ct))
            .RequireAuthorization().ValidarCon<RegistrarDispositivoRequest>().WithTags("Auth");
}
