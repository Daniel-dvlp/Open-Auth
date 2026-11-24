using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EscudoYuma.Api.Shared.Persistencia;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EscudoYuma.Api.Shared.Seguridad;

public sealed class JwtOptions
{
    /// <summary>Secreto HMAC (>= 32 caracteres). En producción SOLO por variable de entorno Jwt__Clave / secretos.</summary>
    public string? Clave { get; set; }
    public string Emisor { get; set; } = "escudo-yuma";
    public string Audiencia { get; set; } = "escudo-yuma-clientes";
    public int MinutosVigencia { get; set; } = 15;
}

public static class Politicas
{
    public const string Analista = "Analista";
    public const string Servicio = "Servicio";
    public const string SistemaOAnalista = "SistemaOAnalista";
}

public sealed record TokenEmitido(string Token, DateTime ExpiraUtc);

/// <summary>
/// Singleton: la clave de firma se deriva una sola vez. En Development, si no hay clave configurada, se genera una
/// efímera por proceso (los tokens dejan de valer al reiniciar). Fuera de Development, la clave es obligatoria.
/// </summary>
public sealed class JwtEmisor
{
    private readonly JwtOptions _opc;
    public SymmetricSecurityKey LlaveFirma { get; }

    public JwtEmisor(IOptions<JwtOptions> opc, IHostEnvironment env, ILogger<JwtEmisor> log)
    {
        _opc = opc.Value;
        var clave = _opc.Clave;
        if (string.IsNullOrWhiteSpace(clave))
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException("Falta el secreto JWT (variable de entorno Jwt__Clave).");
            clave = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            log.LogWarning("Jwt:Clave no configurada: se usa una clave efímera de desarrollo.");
        }
        if (clave.Length < 32) throw new InvalidOperationException("Jwt:Clave debe tener al menos 32 caracteres.");
        // Fuera de Development se rechazan claves triviales (p. ej. "aaaa…" o un valor de ejemplo repetido): HMAC-SHA256 es tan fuerte como su secreto.
        if (!env.IsDevelopment() && clave.Distinct().Count() < 12)
            throw new InvalidOperationException("Jwt:Clave es demasiado predecible: use un secreto aleatorio (p. ej. 48 bytes en base64).");
        LlaveFirma = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(clave));
    }

    public TokenEmitido Emitir(string idCliente, string rol, string idSesion, double scoreIngreso, TimeProvider reloj)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var expira = ahora.AddMinutes(_opc.MinutosVigencia);
        var claims = new[]
        {
            new Claim("sub", idCliente),
            new Claim("sid", idSesion),
            new Claim("rol", rol),
            new Claim("score_ingreso", scoreIngreso.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Double),
            new Claim("jti", Guid.NewGuid().ToString("N")),
        };
        var jwt = new JwtSecurityToken(_opc.Emisor, _opc.Audiencia, claims, ahora, expira,
            new SigningCredentials(LlaveFirma, SecurityAlgorithms.HmacSha256));
        return new TokenEmitido(new JwtSecurityTokenHandler().WriteToken(jwt), expira);
    }
}

/// <summary>Rango admitido para fecha_hora de transacciones y eventos (hora local de Colombia, UTC-5).</summary>
public static class RangoFecha
{
    public static readonly DateTime Minima = new(2020, 1, 1);
    public static readonly TimeSpan ToleranciaFuturo = TimeSpan.FromDays(1);

    public static bool EsValida(string? fechaHora, DateTime ahoraUtc) =>
        DateTime.TryParseExact(fechaHora, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var f)
        && f >= Minima && f <= ahoraUtc.AddHours(-5) + ToleranciaFuturo;
}

/// <summary>Enmascara datos personales antes de loguear/auditar.</summary>
public static class MascaraPii
{
    public static string Llave(string? llave)
    {
        if (string.IsNullOrEmpty(llave)) return "***";
        var l = llave.Trim();
        return l.Length <= 6 ? new string('*', l.Length) : l[..3] + new string('*', l.Length - 6) + l[^3..];
    }
}

/// <summary>
/// Singleton: limitador de ventana deslizante en memoria (por llave e IP). Con varias réplicas debería ir a Redis.
/// La tabla está acotada (<paramref name="maxClaves"/>): un atacante que envíe llaves arbitrarias no puede agotar la memoria ni volver
/// cuadrática la purga. Si la tabla se satura con ventanas vigentes se deniega (falla cerrado) en vez de dejar de limitar.
/// </summary>
public sealed class LimitadorVentana(TimeProvider reloj, int maxClaves = 200_000)
{
    private const int UmbralPurga = 20_000;
    private readonly Dictionary<string, Queue<DateTime>> _eventos = new();
    private readonly object _lock = new();
    private DateTime _ultimaPurga = DateTime.MinValue;

    /// <summary>Registra un intento; devuelve false (y el tiempo de espera) si se superó el máximo en la ventana.</summary>
    public bool Intentar(string clave, int max, TimeSpan ventana, out TimeSpan reintentarEn)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            // La purga recorre toda la tabla: se hace a lo sumo una vez por segundo, no en cada petición.
            if ((_eventos.Count > UmbralPurga || _eventos.Count >= maxClaves) && ahora - _ultimaPurga >= TimeSpan.FromSeconds(1)) { Purgar(ahora, ventana); _ultimaPurga = ahora; }
            if (!_eventos.TryGetValue(clave, out var cola))
            {
                if (_eventos.Count >= maxClaves) { reintentarEn = ventana; return false; }
                _eventos[clave] = cola = new Queue<DateTime>();
            }
            while (cola.Count > 0 && ahora - cola.Peek() >= ventana) cola.Dequeue();
            if (cola.Count >= max)
            {
                reintentarEn = ventana - (ahora - cola.Peek());
                return false;
            }
            cola.Enqueue(ahora);
            reintentarEn = TimeSpan.Zero;
            return true;
        }
    }

    private void Purgar(DateTime ahora, TimeSpan ventana)
    {
        foreach (var k in _eventos.Where(kv => kv.Value.Count == 0 || ahora - kv.Value.Peek() >= ventana).Select(kv => kv.Key).ToList())
            _eventos.Remove(k);
    }
}

/// <summary>Filtro de endpoint que valida el argumento T con FluentValidation y responde 400 ProblemDetails.</summary>
public sealed class ValidacionFiltro<T>(IValidator<T> validador) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var arg = ctx.Arguments.OfType<T>().FirstOrDefault();
        if (arg is null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["cuerpo"] = ["El cuerpo de la solicitud es obligatorio."] });
        var res = await validador.ValidateAsync(arg, ctx.HttpContext.RequestAborted);
        if (!res.IsValid)
            return Results.ValidationProblem(res.ToDictionary(), title: "Solicitud inválida");
        return await next(ctx);
    }
}

public static class SeguridadExtensions
{
    public static RouteHandlerBuilder ValidarCon<T>(this RouteHandlerBuilder b) => b.AddEndpointFilter<ValidacionFiltro<T>>();

    public static IServiceCollection AddSeguridad(this IServiceCollection servicios)
    {
        servicios.AddOptions<JwtOptions>().BindConfiguration("Jwt");
        servicios.AddSingleton<JwtEmisor>();
        servicios.AddSingleton<LimitadorVentana>();

        // La configuración JWT se resuelve de forma diferida (a la primera petición), nunca al construir el host.
        servicios.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        servicios.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtEmisor, IOptions<JwtOptions>>((o, emisor, jwt) =>
            {
                o.MapInboundClaims = false; // se conservan "sub", "rol", "score_ingreso" tal cual
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwt.Value.Emisor,
                    ValidateAudience = true, ValidAudience = jwt.Value.Audiencia,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = emisor.LlaveFirma,
                    ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
                    RequireSignedTokens = true, RequireExpirationTime = true,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256], // algoritmo fijo: ni "none" ni HS384/HS512 con la misma clave
                    NameClaimType = "sub", RoleClaimType = "rol",
                };
            });
        servicios.AddAuthorizationBuilder()
            // Denegar por defecto: todo endpoint sin política explícita exige usuario autenticado (los públicos usan AllowAnonymous).
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Politicas.Analista, p => p.RequireAuthenticatedUser().RequireClaim("rol", Roles.Analista))
            .AddPolicy(Politicas.Servicio, p => p.RequireAuthenticatedUser().RequireClaim("rol", Roles.Servicio))
            .AddPolicy(Politicas.SistemaOAnalista, p => p.RequireAuthenticatedUser().RequireClaim("rol", Roles.Analista, Roles.Servicio));
        return servicios;
    }

    /// <summary>
    /// Cabeceras X-Forwarded-* SOLO de proxies de confianza (Seguridad:ProxiesConfiables = lista de IP o CIDR). Sin esta lista el middleware no se
    /// instala y X-Forwarded-For se ignora por completo: el límite por IP no se puede evadir falsificando la cabecera.
    /// </summary>
    public static IServiceCollection AddProxiesConfiables(this IServiceCollection servicios)
    {
        servicios.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((o, cfg) =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            o.KnownProxies.Clear();
            o.KnownNetworks.Clear();
            foreach (var entrada in cfg.GetSection("Seguridad:ProxiesConfiables").Get<string[]>() ?? [])
            {
                var partes = entrada.Split('/', 2);
                var ip = System.Net.IPAddress.Parse(partes[0].Trim());
                if (partes.Length == 2) o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(ip, int.Parse(partes[1])));
                else o.KnownProxies.Add(ip);
            }
        });
        return servicios;
    }

    /// <summary>
    /// /metrics solo se sirve por el puerto interno Seguridad:Metricas:Puerto (no publicado fuera de la red de contenedores) y ese puerto
    /// solo sirve /metrics. Con Puerto = 0 (por defecto, p. ej. pruebas) no hay restricción.
    /// </summary>
    public static IApplicationBuilder UsePuertoMetricas(this IApplicationBuilder app, int puerto) => app.Use(async (ctx, next) =>
    {
        if (puerto > 0 && ctx.Request.Path.StartsWithSegments("/metrics") != (ctx.Connection.LocalPort == puerto))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next();
    });

    public const long MaxCuerpoBytes = 1_048_576;

    /// <summary>
    /// Tope de tamaño del cuerpo (1 MiB) independiente del servidor: rechaza con 413 por Content-Length y, para cuerpos sin longitud
    /// declarada (chunked), fija el límite en la característica de Kestrel. Evita agotar memoria con JSON enormes.
    /// </summary>
    public static IApplicationBuilder UseLimiteCuerpo(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.Request.ContentLength > MaxCuerpoBytes)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        var f = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (f is { IsReadOnly: false }) f.MaxRequestBodySize = MaxCuerpoBytes;
        await next();
    });

    /// <summary>Cabeceras seguras en toda respuesta (la API solo devuelve JSON).</summary>
    public static IApplicationBuilder UseCabecerasSeguras(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            h["Cache-Control"] = "no-store";
            h["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
            return Task.CompletedTask;
        });
        await next();
    });
}
