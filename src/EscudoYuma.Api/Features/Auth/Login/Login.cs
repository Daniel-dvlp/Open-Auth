using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Observabilidad;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Scoring;
using EscudoYuma.Api.Shared.Seguridad;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EscudoYuma.Api.Features.Auth.Login;

// ---------- Contrato ----------
public sealed record LoginRequest(string Llave, string Clave, string IdDispositivo, string DepartamentoIp);

/// <param name="Resultado">permitir | otp | bloqueo_temporal</param>
public sealed record LoginResponse(string Resultado, double ScoreIngreso, IReadOnlyList<string> Razones, string? Token, DateTime? ExpiraUtc);

public sealed class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Llave).NotEmpty().MaximumLength(64).WithMessage("La llave es obligatoria (máx. 64).");
        RuleFor(x => x.Clave).NotEmpty().MaximumLength(128).WithMessage("La clave es obligatoria (máx. 128).");
        RuleFor(x => x.IdDispositivo).NotEmpty().MaximumLength(64).WithMessage("id_dispositivo es obligatorio (máx. 64).");
        RuleFor(x => x.DepartamentoIp).NotEmpty().MaximumLength(64).WithMessage("departamento_ip es obligatorio (máx. 64).");
    }
}

/// <summary>
/// Umbrales del resultado de login sobre el score de ingreso (training/score_login.py no define umbrales; se fijan aquí):
/// permitir &lt; 0,40 · otp en [0,40; 0,70) · bloqueo_temporal &gt;= 0,70. Con los pesos del score, un dispositivo no visto (0,30)
/// solo exige OTP; para bloquear hacen falta varias señales (p. ej. dispositivo nuevo + fallidos + cambio de SIM).
/// </summary>
public sealed class LoginOptions
{
    public int MaxIntentosPorLlave { get; set; } = 5;
    public int MaxIntentosPorIp { get; set; } = 30;
    public int VentanaSegundos { get; set; } = 300;
    public double UmbralOtp { get; set; } = 0.40;
    public double UmbralBloqueo { get; set; } = 0.70;
}

// ---------- Score de ingreso ----------
public sealed record ResultadoScoreIngreso(double Score, IReadOnlyList<string> Razones);

/// <summary>Puerto del score de ingreso en [0,1] (mayor = más riesgo).</summary>
public interface IScoreIngreso
{
    ResultadoScoreIngreso Calcular(ContextoLogin contexto);
}

/// <summary>
/// Port exacto de <c>puntuar_login</c> de training/score_login.py (determinista y explicable): score = min(1, suma de pesos de las
/// señales activas), cada señal aporta una razón. Singleton sin estado: lee una instantánea del estado en línea (<see cref="ContextoLogin"/>).
///   dispositivo no visto antes (solo si ya hay dispositivos conocidos)  0,30
///   intentos fallidos en la última hora                                0,12 por intento, tope 0,36
///   fallidos extra en 24 h (24 h - 1 h)                                0,03 por intento, tope 0,09
///   cambio de SIM en las últimas 48 h                                  0,25
///   cambio de clave en las últimas 24 h                                0,20
///   registro de dispositivo en las últimas 24 h                        0,10
///   departamento de la IP distinto al habitual (moda, >= 3 obs.)       0,20
///   hora de madrugada (00:00-04:59)                                    0,08
///   conexión fuera de Colombia (extensión .NET; reemplaza la anterior)  0,40
/// </summary>
public sealed class ScoreIngresoLogin : IScoreIngreso
{
    public const double PesoDispositivoNoVisto = 0.30, PesoFallido1h = 0.12, TopeFallido1h = 0.36, PesoFallido24hExtra = 0.03, TopeFallido24hExtra = 0.09;
    public const double PesoCambioSim = 0.25, PesoCambioClave = 0.20, PesoRegistroDispositivo = 0.10, PesoIpDistinta = 0.20, PesoMadrugada = 0.08;
    public const int MinObservacionesIp = 3;
    public const double PesoIpExtranjera = 0.40;

    public ResultadoScoreIngreso Calcular(ContextoLogin c)
    {
        double score = 0;
        var razones = new List<string>();

        if (c.TieneDispositivos && !c.DispositivoConocido)
        {
            score += PesoDispositivoNoVisto;
            razones.Add("Dispositivo no visto antes para este cliente");
        }
        if (c.Fallidos1h > 0)
        {
            score += Math.Min(TopeFallido1h, PesoFallido1h * c.Fallidos1h);
            razones.Add($"{c.Fallidos1h} intento(s) fallido(s) en la última hora");
        }
        var extra = c.Fallidos24h - c.Fallidos1h;
        if (extra > 0)
        {
            score += Math.Min(TopeFallido24hExtra, PesoFallido24hExtra * extra);
            razones.Add($"{extra} intento(s) fallido(s) adicional(es) en 24 horas");
        }
        if (c.CambioSim48h) { score += PesoCambioSim; razones.Add("Cambio de SIM en las últimas 48 horas"); }
        if (c.CambioClave24h) { score += PesoCambioClave; razones.Add("Cambio de clave en las últimas 24 horas"); }
        if (c.Registro24h) { score += PesoRegistroDispositivo; razones.Add("Dispositivo registrado en las últimas 24 horas"); }
        // Extensión de .NET (no está en score_login.py: los datos del caso solo traen departamentos colombianos). Por sí sola exige
        // verificación (0,40 = umbral OTP); con un dispositivo no visto llega a 0,70 = bloqueo temporal. Reemplaza a "IP distinta".
        if (UbicacionIp.EsFueraDeColombia(c.DepartamentoIp))
        {
            score += PesoIpExtranjera;
            razones.Add($"Ingreso desde fuera de Colombia ({c.DepartamentoIp})");
        }
        else if (!string.IsNullOrEmpty(c.DepartamentoIp) && c.IpHabitual is not null && c.ObservacionesIp >= MinObservacionesIp && c.DepartamentoIp != c.IpHabitual)
        {
            score += PesoIpDistinta;
            razones.Add($"Ubicación de la IP ({c.DepartamentoIp}) distinta a la habitual ({c.IpHabitual})");
        }
        if (c.Hora <= 4) { score += PesoMadrugada; razones.Add("Ingreso de madrugada"); }

        if (razones.Count == 0) razones.Add("Sin señales de riesgo en el ingreso");
        return new ResultadoScoreIngreso(Math.Min(1.0, score), razones);
    }
}

// ---------- Caso de uso ----------
public sealed class ManejadorLogin(
    EscudoDbContext db, IPasswordHasher<Usuario> hasher, IScoreIngreso score, IEstadoEnLinea estado, JwtEmisor jwt,
    LimitadorVentana limitador, ServicioAuditoria auditoria, TimeProvider reloj, IOptions<LoginOptions> opciones,
    ILogger<ManejadorLogin> log)
{
    // Hash de relleno: si la llave no existe se verifica igual contra un hash, para no filtrar su existencia por tiempo.
    // Se genera con el hasher inyectado (mismas iteraciones que los hashes reales) la primera vez; la carrera entre dos hilos es inocua.
    private static string? _hashFicticio;
    private string HashFicticio => _hashFicticio ??= hasher.HashPassword(new Usuario(), "relleno-sin-uso");

    private static EventoApp EventoLogin(string idCliente, long ts, string dispositivo, string depto, bool exitoso) =>
        new("LOGIN-" + Guid.NewGuid().ToString("N")[..12], Tiempo.AFechaHora(ts), idCliente, TiposEvento.InicioSesion,
            dispositivo, null, depto, exitoso ? "exitoso" : "fallido");

    public async Task<IResult> EjecutarAsync(LoginRequest req, HttpContext http, CancellationToken ct)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString();
        var opc = opciones.Value;
        var ventana = TimeSpan.FromSeconds(opc.VentanaSegundos);
        var llave = Usuario.NormalizarLlave(req.Llave);
        var enmascarada = MascaraPii.Llave(llave);

        // 1) Rate limiting por llave y por IP (se cuenta cada intento, exista o no la llave).
        if (!limitador.Intentar("login:llave:" + llave, opc.MaxIntentosPorLlave, ventana, out var esperaLlave)
            | !limitador.Intentar("login:ip:" + (ip ?? "desconocida"), opc.MaxIntentosPorIp, ventana, out var esperaIp))
        {
            var espera = (int)Math.Ceiling(Math.Max(esperaLlave.TotalSeconds, esperaIp.TotalSeconds));
            MetricasYuma.Logins.WithLabels("limitado").Inc();
            await auditoria.RegistrarAsync("login_limitado", "auth/login", new { llave = enmascarada }, actor: "anonimo", ct: ct);
            log.LogWarning("Login limitado para llave {Llave}", enmascarada);
            http.Response.Headers.RetryAfter = Math.Max(1, espera).ToString();
            return Results.Problem(title: "Demasiados intentos", detail: "Intente nuevamente más tarde.", statusCode: 429);
        }

        var ahora = reloj.GetUtcNow().UtcDateTime;
        var ts = Tiempo.AEpochColombia(ahora); // hora de Colombia (UTC-5), como los eventos del sandbox
        var usuario = await db.Usuarios.Include(u => u.Dispositivos).FirstOrDefaultAsync(u => u.Llave == llave, ct);

        // 2) Credencial. Mismo mensaje y mismo costo de CPU exista o no la llave.
        var hash = usuario?.HashClave ?? HashFicticio;
        var ok = hasher.VerifyHashedPassword(usuario ?? new Usuario(), hash, req.Clave) != PasswordVerificationResult.Failed && usuario is not null;
        if (!ok)
        {
            // El fallido entra al estado en línea (alimenta login_fallidos_* del modelo y el score de ingreso).
            if (usuario is not null) estado.ProcesarEvento(EventoLogin(usuario.IdCliente, ts, req.IdDispositivo, req.DepartamentoIp, exitoso: false));
            MetricasYuma.Logins.WithLabels("fallido").Inc();
            await auditoria.RegistrarAsync("login_fallido", "auth/login", new { llave = enmascarada }, actor: "anonimo", ct: ct);
            return Results.Problem(title: "Credenciales inválidas", detail: "La llave o la clave no son correctas.", statusCode: 401);
        }

        // 3) Score de ingreso (port de score_login.py) con el estado real. Los dispositivos registrados en la BD cuentan como conocidos
        //    SOLO para este score; no entran al estado de atributos (el modelo se entrenó con "visto" = login exitoso o transacción).
        var contexto = estado.ConsultarLogin(usuario!.IdCliente, ts, req.IdDispositivo, req.DepartamentoIp, usuario.Dispositivos.Select(d => d.IdDispositivo));
        var res = score.Calcular(contexto);
        var resultado = res.Score >= opc.UmbralBloqueo ? "bloqueo_temporal" : res.Score >= opc.UmbralOtp ? "otp" : "permitir";

        // El score de la sesión queda en el estado: NO es atributo del modelo, solo escala la política de transacciones.
        estado.RegistrarScoreIngreso(usuario.IdCliente, res.Score, ahora);

        string? token = null;
        DateTime? expira = null;
        if (resultado == "permitir")
        {
            // Solo un ingreso completo cuenta como "dispositivo visto"; con OTP/bloqueo el dispositivo seguiría sin verificarse.
            estado.ProcesarEvento(EventoLogin(usuario.IdCliente, ts, req.IdDispositivo, req.DepartamentoIp, exitoso: true));
            var idSesion = Guid.NewGuid().ToString("N");
            var emitido = jwt.Emitir(usuario.IdCliente, usuario.Rol, idSesion, res.Score, reloj);
            estado.GuardarSesion(new SesionIngreso(idSesion, usuario.IdCliente, res.Score, emitido.ExpiraUtc));
            token = emitido.Token;
            expira = emitido.ExpiraUtc;
        }

        MetricasYuma.Logins.WithLabels(resultado).Inc();
        await auditoria.RegistrarAsync("login_" + resultado, "auth/login",
            new { llave = enmascarada, score_ingreso = res.Score, razones = res.Razones }, actor: usuario.IdCliente, ct: ct);
        return Results.Ok(new LoginResponse(resultado, res.Score, res.Razones, token, expira));
    }
}

public sealed class LoginFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddOptions<LoginOptions>().BindConfiguration("Seguridad:Login");
        s.AddSingleton<IScoreIngreso, ScoreIngresoLogin>(); // sin estado: Singleton
        s.AddScoped<IValidator<LoginRequest>, LoginValidator>();
        s.AddScoped<ManejadorLogin>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/auth/login", (LoginRequest req, ManejadorLogin h, HttpContext http, CancellationToken ct) =>
                h.EjecutarAsync(req, http, ct))
            .AllowAnonymous().ValidarCon<LoginRequest>().WithTags("Auth");
}
