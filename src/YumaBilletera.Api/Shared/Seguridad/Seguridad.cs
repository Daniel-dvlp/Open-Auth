using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using YumaBilletera.Api.Shared.Persistencia;

namespace YumaBilletera.Api.Shared.Seguridad;

public static class Politicas
{
    public const string Cliente = "Cliente";
}

/// <summary>Bitácora encadenada de la billetera (misma cadena SHA-256 que el Escudo, en su propia base).</summary>
public sealed class ServicioAuditoria(BilleteraDbContext db, ContextoAuditoria ctx, IHttpContextAccessor http, TimeProvider reloj, EstadoCadenaAuditoria cadena,
    IServiceScopeFactory fabrica, ILogger<ServicioAuditoria> log)
    : ServicioAuditoriaBase<BilleteraDbContext>(db, ctx, http, reloj, cadena, fabrica, log);

/// <summary>Datos del cliente autenticado tomados del token (sub = id_cliente, sid = sesión).</summary>
public static class ClaimsCliente
{
    public static string IdCliente(this ClaimsPrincipal u) => u.FindFirst("sub")?.Value ?? throw new InvalidOperationException("Token sin sub.");
    public static string IdSesion(this ClaimsPrincipal u) => u.FindFirst("sid")?.Value ?? throw new InvalidOperationException("Token sin sid.");
}

public static class SeguridadExtensions
{
    public static IServiceCollection AddSeguridad(this IServiceCollection servicios)
    {
        servicios.AddOptions<JwtOptions>().BindConfiguration("Jwt");
        servicios.AddSingleton<JwtEmisor>();
        servicios.AddSingleton<LimitadorVentana>();
        servicios.AddScoped<ContextoAuditoria>();
        servicios.AddSingleton<EstadoCadenaAuditoria>();
        servicios.AddScoped<ServicioAuditoria>();

        servicios.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        servicios.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtEmisor, IOptions<JwtOptions>>((o, emisor, jwt) =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwt.Value.Emisor,
                    ValidateAudience = true, ValidAudience = jwt.Value.Audiencia,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = emisor.LlaveFirma,
                    ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
                    RequireSignedTokens = true, RequireExpirationTime = true,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = "sub", RoleClaimType = "rol",
                };
            });
        servicios.AddAuthorizationBuilder()
            // Denegar por defecto: los endpoints públicos usan AllowAnonymous explícito.
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Politicas.Cliente, p => p.RequireAuthenticatedUser().RequireClaim("rol", Roles.Cliente));
        return servicios;
    }
}
