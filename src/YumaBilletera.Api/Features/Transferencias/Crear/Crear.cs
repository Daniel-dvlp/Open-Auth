using System.Security.Claims;
using YumaBilletera.Api.Shared.Contabilidad;
using YumaBilletera.Api.Shared.Operaciones;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Transferencias.Crear;

// ---------- Contrato ----------
/// <summary>Transferencia inmediata a un destinatario inscrito o directamente a una llave Yuma (celular). Se envía exactamente uno de los dos.</summary>
public sealed record CrearTransferenciaRequest(Guid? IdDestinatario, string? Llave, long MontoCop, string? Descripcion);

public sealed class CrearTransferenciaValidator : AbstractValidator<CrearTransferenciaRequest>
{
    public CrearTransferenciaValidator()
    {
        RuleFor(x => x).Must(x => (x.IdDestinatario is not null) ^ !string.IsNullOrWhiteSpace(x.Llave))
            .WithName("destino").WithMessage("Envía id_destinatario o llave (uno de los dos).");
        RuleFor(x => x.Llave).Must(l => Cliente.NormalizarLlave(l!) is { Length: 10 } n && n[0] == '3')
            .When(x => !string.IsNullOrWhiteSpace(x.Llave)).WithMessage("La llave es un celular colombiano de 10 dígitos que empieza por 3.");
        RuleFor(x => x.MontoCop).GreaterThan(0).LessThanOrEqualTo(LimitesOperacion.MaxMontoCop)
            .WithMessage("El monto debe ser un valor en pesos mayor que cero.");
        RuleFor(x => x.Descripcion).MaximumLength(80);
    }
}

// ---------- Caso de uso ----------
/// <summary>Resuelve el destino (cliente Yuma o producto de otra entidad) y entrega la operación al pipeline común.</summary>
public sealed class ManejadorCrearTransferencia(BilleteraDbContext db, ProcesadorOperacion procesador)
{
    public async Task<IResult> EjecutarAsync(CrearTransferenciaRequest req, ClaimsPrincipal usuario, HttpContext http, CancellationToken ct)
    {
        var clave = EndpointsOperacion.LeerClave(http);
        if (clave is null) return EndpointsOperacion.FaltaClave();
        var idCliente = usuario.IdCliente();

        string tipoDestino, idDestino, cuentaCredito;
        if (req.IdDestinatario is { } idDest)
        {
            var d = await db.Destinatarios.AsNoTracking().FirstOrDefaultAsync(x => x.Id == idDest && x.IdCliente == idCliente, ct);
            if (d is null) return Results.Problem(title: "Destinatario no encontrado.", statusCode: 404);
            (tipoDestino, idDestino) = (d.TipoDestino, d.IdDestino);
            cuentaCredito = d.TipoDestino == "cuenta_yuma" ? d.IdDestino : CuentasInternas.Puente(d.TipoDestino);
        }
        else
        {
            var llave = Cliente.NormalizarLlave(req.Llave!);
            var destino = await db.Clientes.AsNoTracking().FirstOrDefaultAsync(c => c.Llave == llave, ct);
            if (destino is null) return Results.Problem(title: "La llave no está registrada en Yuma Pay.", statusCode: 404);
            (tipoDestino, idDestino, cuentaCredito) = ("cuenta_yuma", destino.IdCliente, destino.IdCliente);
        }

        return await EndpointsOperacion.ResponderAsync(() => procesador.EjecutarAsync(new SolicitudOperacion(
            idCliente, usuario.IdSesion(), clave, "transferencia_inmediata", tipoDestino, idDestino, cuentaCredito, req.MontoCop, req.Descripcion?.Trim()), ct));
    }
}

public sealed class CrearTransferenciaFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<CrearTransferenciaRequest>, CrearTransferenciaValidator>();
        s.AddScoped<ManejadorCrearTransferencia>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/transferencias", (CrearTransferenciaRequest req, ClaimsPrincipal u, HttpContext http, ManejadorCrearTransferencia h, CancellationToken ct) =>
                h.EjecutarAsync(req, u, http, ct))
            .RequireAuthorization(Politicas.Cliente).ValidarCon<CrearTransferenciaRequest>().WithTags("Transferencias");
}
