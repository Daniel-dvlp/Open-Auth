using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace YumaBilletera.Api.Tests;

public class SeguridadTests
{
    /// <summary>Rutas públicas permitidas. Cualquier ruta nueva sin autorización explícita hace fallar esta prueba (como en el Escudo).</summary>
    private static readonly HashSet<string> Publicas =
    [
        "POST /v1/auth/login", "POST /v1/auth/otp", "POST /v1/auth/biometria", "POST /v1/auth/refresh", "GET /metrics",
        "GET /v1/dev/otp/{id:guid}", "POST /v1/dev/cambio-sim", // solo existen en Development
        "POST /v1/internas/resoluciones", // aviso del Escudo: autenticado con firma HMAC, no con token de cliente
    ];

    [Fact]
    public void Toda_ruta_exige_cliente_salvo_las_publicas_conocidas()
    {
        using var f = new FabricaBilletera();
        _ = f.Server;
        var rutas = f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(rutas);
        foreach (var r in rutas)
        {
            var metodo = r.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
            var clave = $"{metodo} {r.RoutePattern.RawText}";
            var anonimo = r.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            if (Publicas.Contains(clave)) continue;
            Assert.False(anonimo, $"{clave} es anónima y no está en la lista de rutas públicas.");
            var politicas = r.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            Assert.Contains("Cliente", politicas);
        }
    }

    [Theory]
    [InlineData("/v1/cuentas/me")]
    [InlineData("/v1/cuentas/me/movimientos")]
    [InlineData("/v1/destinatarios")]
    [InlineData("/v1/dispositivos")]
    [InlineData("/v1/transferencias/TY1")]
    public async Task Sin_token_responde_401(string ruta)
    {
        using var f = new FabricaBilletera();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync(ruta)).StatusCode);
    }

    [Fact]
    public async Task Cabeceras_seguras_y_correlacion()
    {
        using var f = new FabricaBilletera();
        var r = await f.CreateClient().GetAsync("/v1/cuentas/me");
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(r.Headers.Contains("X-Correlation-Id"));
    }

    [Fact]
    public void Herramientas_de_desarrollo_no_existen_fuera_de_Development()
    {
        using var f = new FabricaProduccion();
        _ = f.Server;
        var rutas = f.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        Assert.DoesNotContain(rutas, r => r!.StartsWith("/v1/dev", StringComparison.Ordinal));
    }

    private sealed class FabricaProduccion : FabricaBilletera
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
        }
    }
}
