using System.Reflection;

namespace EscudoYuma.Api.Shared.Features;

/// <summary>
/// Una "feature" (caso de uso) de la arquitectura vertical slice: registra sus servicios y mapea su endpoint.
/// Program.cs solo compone: AddFeatures() / MapFeatures().
/// </summary>
public interface IFeature
{
    void AddServices(IServiceCollection servicios);
    void MapEndpoints(IEndpointRouteBuilder rutas);
}

public static class FeatureExtensions
{
    private static IEnumerable<IFeature> Descubrir() =>
        typeof(IFeature).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IFeature).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (IFeature)Activator.CreateInstance(t)!);

    public static IServiceCollection AddFeatures(this IServiceCollection servicios)
    {
        foreach (var f in Descubrir()) f.AddServices(servicios);
        return servicios;
    }

    public static IEndpointRouteBuilder MapFeatures(this IEndpointRouteBuilder rutas)
    {
        foreach (var f in Descubrir()) f.MapEndpoints(rutas);
        return rutas;
    }
}
