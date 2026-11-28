using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using YumaBilletera.Api.Shared.AutenticacionReforzada;
using YumaBilletera.Api.Shared.Escudo;
using YumaBilletera.Api.Shared.Operaciones;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;
using YumaBilletera.Api.Shared.Sesiones;

namespace YumaBilletera.Api.Features.Auth.Login;

// ---------- Contrato ----------
/// <param name="Llave">Celular de 10 dígitos (se aceptan espacios: "300 000 0001").</param>
/// <param name="DepartamentoIp">Departamento estimado de la IP (lo resuelve la app/gateway), p. ej. "Antioquia".</param>
public sealed record LoginRequest(string Llave, string Clave, string IdDispositivo, string DepartamentoIp, string? SistemaOperativo);

/// <param name="Resultado">permitir | verificacion_requerida | bloqueo_temporal</param>
public sealed record LoginResponse(
    string Resultado, string Mensaje, string? Token, DateTime? ExpiraUtc, string? RefreshToken, DateTime? RefreshExpiraUtc, VerificacionPendiente? Verificacion);

public sealed class LoginOptions
{
    public int MaxIntentosPorLlave { get; set; } = 5;
    public int MaxIntentosPorIp { get; set; } = 30;
    public int VentanaSegundos { get; set; } = 300;
}

public sealed class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Llave).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Clave).NotEmpty().MaximumLength(128);
        RuleFor(x => x.IdDispositivo).NotEmpty().MaximumLength(64);
        RuleFor(x => x.DepartamentoIp).NotEmpty().MaximumLength(64);
        RuleFor(x => x.SistemaOperativo).MaximumLength(16);
    }
}

// ---------- Caso de uso ----------
/// <summary>
/// Ingreso a la billetera: valida llave y clave, pide al Escudo el score de ingreso y, según el resultado, abre la sesión, exige
/// autenticación reforzada o bloquea temporalmente. OTP por defecto; si hubo cambio de SIM en las 48 h previas el OTP no sirve
/// (podría interceptarse con la SIM duplicada) y se exige biometría facial. Si el Escudo no responde se exige verificación (falla cerrada).
/// </summary>
public sealed class ManejadorLogin(
    BilleteraDbContext db, IPasswordHasher<Cliente> hasher, IEscudoCliente escudo, ConstructorSenales senales, ServicioVerificacion verificaciones,
    EmisorSesion emisor, PublicadorEventos publicador, DespachadorBandeja bandeja, LimitadorVentana limitador, ServicioAuditoria auditoria, TimeProvider reloj,
    IOptions<LoginOptions> opciones, ILogger<ManejadorLogin> log)
{
    private static string? _hashFicticio;
    private string HashFicticio => _hashFicticio ??= hasher.HashPassword(new Cliente(), "relleno-sin-uso");

    public async Task<IResult> EjecutarAsync(LoginRequest req, HttpContext http, CancellationToken ct)
    {
        var opc = opciones.Value;
        var ventana = TimeSpan.FromSeconds(opc.VentanaSegundos);
        var llave = Cliente.NormalizarLlave(req.Llave);
        var enmascarada = MascaraPii.Llave(llave);
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "desconocida";

        // 1) Límite de intentos por llave y por IP.
        if (!limitador.Intentar("login:llave:" + llave, opc.MaxIntentosPorLlave, ventana, out var e1)
            | !limitador.Intentar("login:ip:" + ip, opc.MaxIntentosPorIp, ventana, out var e2))
        {
            MetricasBilletera.Ingresos.WithLabels("limitado").Inc();
            await auditoria.RegistrarAsync("login_limitado", "auth/login", new { llave = enmascarada }, actor: "anonimo", ct: ct);
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(Math.Max(e1.TotalSeconds, e2.TotalSeconds))).ToString();
            return Results.Problem(title: "Demasiados intentos", detail: "Intenta de nuevo más tarde.", statusCode: 429);
        }

        // 2) Credencial: mismo mensaje y mismo costo exista o no la llave.
        var cliente = await db.Clientes.AsNoTracking().FirstOrDefaultAsync(c => c.Llave == llave, ct);
        var ok = hasher.VerifyHashedPassword(cliente ?? new Cliente(), cliente?.HashClave ?? HashFicticio, req.Clave) != PasswordVerificationResult.Failed
                 && cliente is not null;
        if (!ok)
        {
            if (cliente is not null) // el fallido alimenta login_fallidos_* del Escudo
            {
                publicador.Encolar(cliente.IdCliente, TiposEventoApp.InicioSesion, req.IdDispositivo, req.SistemaOperativo, req.DepartamentoIp, exitoso: false);
                await db.SaveChangesAsync(ct);
            }
            MetricasBilletera.Ingresos.WithLabels("fallido").Inc();
            await auditoria.RegistrarAsync("login_fallido", "auth/login", new { llave = enmascarada }, actor: "anonimo", ct: ct);
            return Results.Problem(title: "Credenciales inválidas", detail: "El celular o la clave no son correctos.", statusCode: 401);
        }

        if (cliente is null) return Results.Problem(statusCode: 401); // inalcanzable: ok exige cliente; informa al análisis de nulos
        // 3) Score de ingreso del Escudo (dispositivos registrados en la billetera cuentan como conocidos).
        var conocidos = await db.Dispositivos.AsNoTracking().Where(d => d.IdCliente == cliente!.IdCliente).Select(d => d.IdDispositivo).ToListAsync(ct);
        ResultadoIngresoEscudo? res = null;
        await ProcesadorOperacion.EntregarEventosPreviosAsync(bandeja, cliente.IdCliente, ct); // los fallidos previos cuentan en el score
        try { res = await escudo.EvaluarIngresoAsync(new IngresoEscudo(cliente!.IdCliente, req.IdDispositivo, req.DepartamentoIp, conocidos), ct); }
        catch (Exception ex) when ((ex is EscudoNoDisponibleException or HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            MetricasBilletera.FallasEscudo.WithLabels("ingreso").Inc();
            log.LogWarning(ex, "Escudo sin respuesta en el ingreso: se exige verificación reforzada (falla cerrada).");
        }

        var ahora = reloj.GetUtcNow().UtcDateTime;
        var resultado = res?.Resultado ?? ResultadosIngreso.Otp;
        if (resultado == ResultadosIngreso.BloqueoTemporal)
        {
            publicador.Encolar(cliente.IdCliente, TiposEventoApp.InicioSesion, req.IdDispositivo, req.SistemaOperativo, req.DepartamentoIp, exitoso: false);
            await db.SaveChangesAsync(ct);
            MetricasBilletera.Ingresos.WithLabels("bloqueo_temporal").Inc();
            await auditoria.RegistrarAsync("login_bloqueo_temporal", "auth/login", new { llave = enmascarada, score_ingreso = res?.ScoreIngreso, razones = res?.Razones }, actor: cliente.IdCliente, ct: ct);
            return Results.Json(new LoginResponse("bloqueo_temporal",
                "Por tu seguridad bloqueamos temporalmente el ingreso. Intenta más tarde o comunícate con Yuma Pay.", null, null, null, null, null),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var sesion = new SesionCliente
        {
            IdSesion = Guid.NewGuid().ToString("N"), IdCliente = cliente.IdCliente, IdDispositivo = req.IdDispositivo, DepartamentoIp = req.DepartamentoIp,
            ScoreIngreso = res?.ScoreIngreso ?? 1.0, CreadaUtc = ahora, ExpiraUtc = ahora.AddMinutes(10),
        };
        db.Sesiones.Add(sesion);

        if (resultado == ResultadosIngreso.Permitir)
        {
            var tokens = emisor.Activar(sesion);
            await db.SaveChangesAsync(ct);
            MetricasBilletera.Ingresos.WithLabels("permitir").Inc();
            await auditoria.RegistrarAsync("login_permitir", "auth/login", new { llave = enmascarada, score_ingreso = sesion.ScoreIngreso, razones = res!.Razones }, actor: cliente.IdCliente, ct: ct);
            return Results.Ok(new LoginResponse("permitir", "Bienvenido a Yuma Pay.", tokens.Token, tokens.ExpiraUtc, tokens.RefreshToken, tokens.RefreshExpiraUtc, null));
        }

        // 4) Verificación reforzada: OTP, o biometría si hubo cambio de SIM en 48 h (el OTP por SMS podría llegar a la SIM duplicada).
        var simReciente = await senales.CambioSim48hAsync(cliente.IdCliente, ahora, ct) == 1;
        var metodo = simReciente ? MetodosVerificacion.Biometria : MetodosVerificacion.Otp;
        sesion.Estado = simReciente ? EstadosSesion.PendienteBiometria : EstadosSesion.PendienteOtp;
        Verificacion v;
        try { v = await verificaciones.CrearAsync(PropositosVerificacion.Ingreso, metodo, sesion.IdSesion, cliente.IdCliente, ct); }
        catch (InvalidOperationException ex)
        {
            log.LogError(ex, "No se pudo enviar el OTP.");
            return Results.Problem(title: "Verificación no disponible", detail: "No pudimos enviar el código. Intenta más tarde.", statusCode: 503);
        }
        await db.SaveChangesAsync(ct);
        MetricasBilletera.Ingresos.WithLabels("verificacion_" + metodo).Inc();
        await auditoria.RegistrarAsync("login_verificacion_requerida", "auth/login", new
        {
            llave = enmascarada, metodo, score_ingreso = res?.ScoreIngreso, razones = res?.Razones, escudo_disponible = res is not null, cambio_sim_48h = simReciente,
        }, actor: cliente.IdCliente, ct: ct);
        var mensaje = simReciente
            ? "Detectamos un cambio reciente de SIM. Por tu seguridad confirma tu identidad con la verificación facial."
            : "Te enviamos un código de 6 dígitos por SMS. Ingrésalo para continuar.";
        return Results.Ok(new LoginResponse("verificacion_requerida", mensaje, null, null, null, null, new VerificacionPendiente(v.Id, v.Metodo, v.ExpiraUtc)));
    }
}

public sealed class LoginFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddOptions<LoginOptions>().BindConfiguration("Seguridad:Login");
        s.AddScoped<IValidator<LoginRequest>, LoginValidator>();
        s.AddScoped<ManejadorLogin>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/auth/login", (LoginRequest req, ManejadorLogin h, HttpContext http, CancellationToken ct) => h.EjecutarAsync(req, http, ct))
            .AllowAnonymous().ValidarCon<LoginRequest>().WithTags("Auth");
}
