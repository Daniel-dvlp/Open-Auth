using System.Security.Claims;
using YumaBilletera.Api.Shared.Contabilidad;
using YumaBilletera.Api.Shared.Operaciones;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Recargas.RecargarCelular;

/// <param name="IdOperador">Operador de telefonía (formato de los datos del caso: OPE + 2 dígitos).</param>
/// <param name="Celular">Celular colombiano de 10 dígitos que se recarga.</param>
public sealed record RecargarCelularRequest(string IdOperador, string Celular, long MontoCop);

public sealed class RecargarCelularValidator : AbstractValidator<RecargarCelularRequest>
{
    public RecargarCelularValidator()
    {
        RuleFor(x => x.IdOperador).NotEmpty().Matches("^OPE[0-9]{2}$").WithMessage("Operador inválido (OPE + 2 dígitos).");
        RuleFor(x => x.Celular).NotEmpty().Must(c => Cliente.NormalizarLlave(c) is { Length: 10 } n && n[0] == '3')
            .WithMessage("El celular tiene 10 dígitos y empieza por 3.");
        RuleFor(x => x.MontoCop).GreaterThan(0).LessThanOrEqualTo(LimitesOperacion.MaxMontoCop);
    }
}

/// <summary>Recarga de celular (recarga_celular → operador).</summary>
public sealed class ManejadorRecargarCelular(ProcesadorOperacion procesador)
{
    public Task<IResult> EjecutarAsync(RecargarCelularRequest req, ClaimsPrincipal u, HttpContext http, CancellationToken ct)
    {
        var clave = EndpointsOperacion.LeerClave(http);
        if (clave is null) return Task.FromResult(EndpointsOperacion.FaltaClave());
        return EndpointsOperacion.ResponderAsync(() => procesador.EjecutarAsync(new SolicitudOperacion(
            u.IdCliente(), u.IdSesion(), clave, "recarga_celular", "operador", req.IdOperador, CuentasInternas.Puente("operador"), req.MontoCop,
            "Recarga " + MascaraPii.Llave(Cliente.NormalizarLlave(req.Celular))), ct));
    }
}

public sealed class RecargarCelularFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<RecargarCelularRequest>, RecargarCelularValidator>();
        s.AddScoped<ManejadorRecargarCelular>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/recargas", (RecargarCelularRequest req, ClaimsPrincipal u, HttpContext http, ManejadorRecargarCelular h, CancellationToken ct) =>
                h.EjecutarAsync(req, u, http, ct))
            .RequireAuthorization(Politicas.Cliente).ValidarCon<RecargarCelularRequest>().WithTags("Recargas");
}
