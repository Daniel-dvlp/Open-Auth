using YumaBilletera.Api.Shared.AutenticacionReforzada;
using YumaBilletera.Api.Shared.Escudo;
using YumaBilletera.Api.Shared.Operaciones;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;
using YumaBilletera.Api.Shared.Sesiones;

namespace YumaBilletera.Api.Features.Auth.ConfirmarIngreso;

// ---------- Contrato ----------
public sealed record ConfirmarOtpRequest(Guid IdVerificacion, string Codigo);
/// <param name="ResultadoSimulado">supera | no_supera (biometría simulada: no hay proveedor real en el alcance).</param>
public sealed record ConfirmarBiometriaRequest(Guid IdVerificacion, string ResultadoSimulado);
public sealed record ConfirmarIngresoResponse(string Resultado, string Mensaje, string? Token, DateTime? ExpiraUtc, string? RefreshToken, DateTime? RefreshExpiraUtc);

public sealed class ConfirmarOtpValidator : AbstractValidator<ConfirmarOtpRequest>
{
    public ConfirmarOtpValidator()
    {
        RuleFor(x => x.IdVerificacion).NotEmpty();
        RuleFor(x => x.Codigo).NotEmpty().Length(6).Matches("^[0-9]{6}$").WithMessage("El código tiene 6 dígitos.");
    }
}

public sealed class ConfirmarBiometriaValidator : AbstractValidator<ConfirmarBiometriaRequest>
{
    public ConfirmarBiometriaValidator()
    {
        RuleFor(x => x.IdVerificacion).NotEmpty();
        RuleFor(x => x.ResultadoSimulado).Must(r => r is BiometriaSimulada.Supera or BiometriaSimulada.NoSupera)
            .WithMessage("resultado_simulado debe ser supera o no_supera.");
    }
}

// ---------- Caso de uso ----------
/// <summary>Completa un ingreso que quedó pendiente de autenticación reforzada (OTP o biometría facial).</summary>
public sealed class ManejadorConfirmarIngreso(
    BilleteraDbContext db, ServicioVerificacion verificaciones, EmisorSesion emisor, PublicadorEventos publicador, LimitadorVentana limitador,
    ServicioAuditoria auditoria)
{
    public Task<IResult> OtpAsync(ConfirmarOtpRequest req, CancellationToken ct) =>
        ConfirmarAsync(req.IdVerificacion, MetodosVerificacion.Otp, v => Task.FromResult(verificaciones.ValidarOtp(v, req.Codigo)), ct);

    public Task<IResult> BiometriaAsync(ConfirmarBiometriaRequest req, CancellationToken ct) =>
        ConfirmarAsync(req.IdVerificacion, MetodosVerificacion.Biometria,
            async v => (await verificaciones.ValidarBiometriaAsync(v, req.ResultadoSimulado, ct)).Estado, ct);

    private async Task<IResult> ConfirmarAsync(Guid id, string metodo, Func<Verificacion, Task<EstadoIntento>> validar, CancellationToken ct)
    {
        if (!limitador.Intentar("ingreso:verificacion:" + id, 10, TimeSpan.FromMinutes(5), out _))
            return Results.Problem(title: "Demasiados intentos", statusCode: 429);
        var v = await db.Verificaciones.FirstOrDefaultAsync(x => x.Id == id && x.Proposito == PropositosVerificacion.Ingreso && x.Metodo == metodo, ct);
        var sesion = v is null ? null : await db.Sesiones.FirstOrDefaultAsync(s => s.IdSesion == v.Referencia, ct);
        if (v is null || sesion is null || sesion.Estado is not (EstadosSesion.PendienteOtp or EstadosSesion.PendienteBiometria))
            return Results.Problem(title: "Verificación no encontrada", statusCode: 404);

        var estado = await validar(v);
        if (estado == EstadoIntento.Valido)
        {
            var tokens = emisor.Activar(sesion);
            await db.SaveChangesAsync(ct);
            MetricasBilletera.Ingresos.WithLabels("permitir_tras_" + metodo).Inc();
            await auditoria.RegistrarAsync("login_verificado", "auth/" + metodo, new { metodo }, actor: sesion.IdCliente, ct: ct);
            return Results.Ok(new ConfirmarIngresoResponse("permitir", "Bienvenido a Yuma Pay.", tokens.Token, tokens.ExpiraUtc, tokens.RefreshToken, tokens.RefreshExpiraUtc));
        }

        if (estado is EstadoIntento.Agotada or EstadoIntento.Expirada)
        {
            sesion.Estado = EstadosSesion.Cerrada; // hay que volver a ingresar
            publicador.Encolar(sesion.IdCliente, TiposEventoApp.InicioSesion, sesion.IdDispositivo, null, sesion.DepartamentoIp, exitoso: false);
        }
        await db.SaveChangesAsync(ct);
        await auditoria.RegistrarAsync("login_verificacion_fallida", "auth/" + metodo, new { metodo, estado = estado.ToString() }, actor: sesion.IdCliente, ct: ct);
        return estado switch
        {
            EstadoIntento.Invalido => Results.Problem(title: "Código incorrecto", detail: "Revisa el código e inténtalo de nuevo.", statusCode: 401),
            EstadoIntento.Expirada => Results.Problem(title: "Verificación vencida", detail: "Vuelve a ingresar para recibir una nueva verificación.", statusCode: 410),
            _ => Results.Problem(title: "Verificación no superada", detail: "No pudimos confirmar tu identidad. Vuelve a ingresar.", statusCode: 403),
        };
    }
}

public sealed class ConfirmarIngresoFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<ConfirmarOtpRequest>, ConfirmarOtpValidator>();
        s.AddScoped<IValidator<ConfirmarBiometriaRequest>, ConfirmarBiometriaValidator>();
        s.AddScoped<ManejadorConfirmarIngreso>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        r.MapPost("/v1/auth/otp", (ConfirmarOtpRequest req, ManejadorConfirmarIngreso h, CancellationToken ct) => h.OtpAsync(req, ct))
            .AllowAnonymous().ValidarCon<ConfirmarOtpRequest>().WithTags("Auth");
        r.MapPost("/v1/auth/biometria", (ConfirmarBiometriaRequest req, ManejadorConfirmarIngreso h, CancellationToken ct) => h.BiometriaAsync(req, ct))
            .AllowAnonymous().ValidarCon<ConfirmarBiometriaRequest>().WithTags("Auth");
    }
}
