using System.Security.Claims;
using YumaBilletera.Api.Shared.Contabilidad;
using YumaBilletera.Api.Shared.Operaciones;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Pagos.PagarQr;

/// <param name="CodigoComercio">Código del comercio leído del QR (formato de los datos del caso: COM + 4 dígitos, p. ej. COM0412).</param>
public sealed record PagarQrRequest(string CodigoComercio, long MontoCop);

public sealed class PagarQrValidator : AbstractValidator<PagarQrRequest>
{
    public PagarQrValidator()
    {
        RuleFor(x => x.CodigoComercio).NotEmpty().Matches("^COM[0-9]{4}$").WithMessage("Código de comercio inválido (COM + 4 dígitos).");
        RuleFor(x => x.MontoCop).GreaterThan(0).LessThanOrEqualTo(LimitesOperacion.MaxMontoCop);
    }
}

/// <summary>Pago con QR a un comercio (pago_qr → comercio). Modalidad de fraude asociada: engaño con QR a comercio nuevo.</summary>
public sealed class ManejadorPagarQr(ProcesadorOperacion procesador)
{
    public Task<IResult> EjecutarAsync(PagarQrRequest req, ClaimsPrincipal u, HttpContext http, CancellationToken ct)
    {
        var clave = EndpointsOperacion.LeerClave(http);
        if (clave is null) return Task.FromResult(EndpointsOperacion.FaltaClave());
        return EndpointsOperacion.ResponderAsync(() => procesador.EjecutarAsync(new SolicitudOperacion(
            u.IdCliente(), u.IdSesion(), clave, "pago_qr", "comercio", req.CodigoComercio, CuentasInternas.Puente("comercio"), req.MontoCop,
            "Pago QR " + req.CodigoComercio), ct));
    }
}

public sealed class PagarQrFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<PagarQrRequest>, PagarQrValidator>();
        s.AddScoped<ManejadorPagarQr>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/pagos/qr", (PagarQrRequest req, ClaimsPrincipal u, HttpContext http, ManejadorPagarQr h, CancellationToken ct) => h.EjecutarAsync(req, u, http, ct))
            .RequireAuthorization(Politicas.Cliente).ValidarCon<PagarQrRequest>().WithTags("Pagos");
}
