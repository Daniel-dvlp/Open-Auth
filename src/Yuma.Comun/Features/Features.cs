using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Yuma.Comun.Features;

/// <summary>
/// Una "feature" (caso de uso) de la arquitectura vertical slice: registra sus servicios y mapea su endpoint.
/// Program.cs solo compone: AddFeatures(ensamblado) / MapFeatures(ensamblado).
/// </summary>
public interface IFeature
{
    void AddServices(IServiceCollection servicios);
    void MapEndpoints(IEndpointRouteBuilder rutas);
}

public static class FeatureExtensions
{
    /// <summary>Features concretas del ensamblado, en orden estable (nombre completo).</summary>
    public static IEnumerable<IFeature> Descubrir(Assembly ensamblado) =>
        ensamblado.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IFeature).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (IFeature)Activator.CreateInstance(t)!);

    public static IServiceCollection AddFeatures(this IServiceCollection servicios, Assembly ensamblado)
    {
        foreach (var f in Descubrir(ensamblado)) f.AddServices(servicios);
        return servicios;
    }

    public static IEndpointRouteBuilder MapFeatures(this IEndpointRouteBuilder rutas, Assembly ensamblado)
    {
        foreach (var f in Descubrir(ensamblado)) f.MapEndpoints(rutas);
        return rutas;
    }
}
