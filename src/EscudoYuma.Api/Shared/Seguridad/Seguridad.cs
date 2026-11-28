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

public static class Politicas
{
    public const string Analista = "Analista";
    public const string Servicio = "Servicio";
    public const string SistemaOAnalista = "SistemaOAnalista";
}

public static class SeguridadExtensions
{
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
}
