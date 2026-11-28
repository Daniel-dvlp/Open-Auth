using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Yuma.Comun.Web;
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

public static class WebExtensions
{
    public static RouteHandlerBuilder ValidarCon<T>(this RouteHandlerBuilder b) => b.AddEndpointFilter<ValidacionFiltro<T>>();
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
