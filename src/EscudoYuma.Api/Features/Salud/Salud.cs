using EscudoYuma.Api.Shared.Persistencia;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace EscudoYuma.Api.Features.Salud;

public sealed class SaludBd(IServiceScopeFactory fabrica) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        using var scope = fabrica.CreateScope();
        var ok = await scope.ServiceProvider.GetRequiredService<EscudoDbContext>().Database.CanConnectAsync(ct);
        return ok ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Sin conexión a la base de datos.");
    }
}

/// <summary>Solo se evalúa si Redis está configurado (IConnectionMultiplexer registrado).</summary>
public sealed class SaludRedis(IServiceProvider sp) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var mux = sp.GetService<IConnectionMultiplexer>();
        if (mux is null) return HealthCheckResult.Healthy("Redis no configurado.");
        try { await mux.GetDatabase().PingAsync(); return HealthCheckResult.Healthy(); }
        catch (Exception ex) { return HealthCheckResult.Degraded("Redis no responde.", ex); } // el estado en memoria sigue funcionando
    }
}

public static class RedisExtensions
{
    /// <summary>IConnectionMultiplexer es Singleton: una única conexión multiplexada por proceso (es thread-safe y costosa).</summary>
    public static IServiceCollection AddRedisOpcional(this IServiceCollection s, string conexion)
    {
        s.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var opc = ConfigurationOptions.Parse(conexion);
            opc.AbortOnConnectFail = false; // la API arranca aunque Redis tarde
            return ConnectionMultiplexer.Connect(opc);
        });
        return s;
    }
}

public sealed class SaludFeature : IFeature
{
    public void AddServices(IServiceCollection s) =>
        s.AddHealthChecks()
            .AddCheck<SaludBd>("base_de_datos", tags: ["ready"])
            .AddCheck<SaludRedis>("redis", tags: ["ready"]);

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        r.MapGet("/v1/salud", () => Results.Ok(new { estado = "ok", servicio = "escudo-yuma" })).AllowAnonymous().WithTags("Salud");
        r.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        r.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
    }
}
