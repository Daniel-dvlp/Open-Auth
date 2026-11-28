using System.Security.Claims;
using YumaBilletera.Api.Shared.Contabilidad;
using YumaBilletera.Api.Shared.Operaciones;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Pagos.PagarServicios;

/// <param name="IdServicio">Convenio del servicio (formato de los datos del caso: SRV + 2 dígitos).</param>
/// <param name="ReferenciaPago">Referencia de la factura (solo dígitos).</param>
public sealed record PagarServiciosRequest(string IdServicio, string ReferenciaPago, long MontoCop);

public sealed class PagarServiciosValidator : AbstractValidator<PagarServiciosRequest>
{
    public PagarServiciosValidator()
    {
        RuleFor(x => x.IdServicio).NotEmpty().Matches("^SRV[0-9]{2}$").WithMessage("Servicio inválido (SRV + 2 dígitos).");
        RuleFor(x => x.ReferenciaPago).NotEmpty().Matches("^[0-9]{4,30}$").WithMessage("La referencia de pago tiene entre 4 y 30 dígitos.");
        RuleFor(x => x.MontoCop).GreaterThan(0).LessThanOrEqualTo(LimitesOperacion.MaxMontoCop);
    }
}

/// <summary>Pago de servicios públicos o privados (pago_servicios → servicio).</summary>
public sealed class ManejadorPagarServicios(ProcesadorOperacion procesador)
{
    public Task<IResult> EjecutarAsync(PagarServiciosRequest req, ClaimsPrincipal u, HttpContext http, CancellationToken ct)
    {
        var clave = EndpointsOperacion.LeerClave(http);
        if (clave is null) return Task.FromResult(EndpointsOperacion.FaltaClave());
        return EndpointsOperacion.ResponderAsync(() => procesador.EjecutarAsync(new SolicitudOperacion(
            u.IdCliente(), u.IdSesion(), clave, "pago_servicios", "servicio", req.IdServicio, CuentasInternas.Puente("servicio"), req.MontoCop,
            $"Pago {req.IdServicio} ref. {req.ReferenciaPago}"), ct));
    }
}

public sealed class PagarServiciosFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<PagarServiciosRequest>, PagarServiciosValidator>();
        s.AddScoped<ManejadorPagarServicios>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/pagos/servicios", (PagarServiciosRequest req, ClaimsPrincipal u, HttpContext http, ManejadorPagarServicios h, CancellationToken ct) =>
                h.EjecutarAsync(req, u, http, ct))
            .RequireAuthorization(Politicas.Cliente).ValidarCon<PagarServiciosRequest>().WithTags("Pagos");
}
