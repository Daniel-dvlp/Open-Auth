using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Features.Destinatarios.Inscribir;

// ---------- Contrato ----------
/// <param name="TipoDestino">cuenta_yuma (por llave/celular) | otra_entidad (entidad + número de producto).</param>
public sealed record InscribirDestinatarioRequest(string Alias, string TipoDestino, string? Llave, string? Entidad, string? NumeroProducto);

public sealed record DestinatarioDto(Guid Id, string Alias, string TipoDestino, string IdDestino, string? Entidad, string? Producto, DateTime InscritoUtc)
{
    public static DestinatarioDto De(Destinatario d) =>
        new(d.Id, d.Alias, d.TipoDestino, d.IdDestino, d.Entidad, d.ProductoEnmascarado, d.InscritoUtc);
}

/// <summary>
/// Catálogo de entidades financieras receptoras para <c>otra_entidad</c>. Se define por configuración (Billetera:EntidadesReceptoras);
/// si está vacío se acepta el nombre de la entidad como texto libre (los datos del caso no incluyen el catálogo).
/// </summary>
public sealed class DestinatariosOptions
{
    public List<string> EntidadesReceptoras { get; set; } = new();
}

public sealed class InscribirDestinatarioValidator : AbstractValidator<InscribirDestinatarioRequest>
{
    public InscribirDestinatarioValidator(IOptions<DestinatariosOptions> opc)
    {
        RuleFor(x => x.Alias).NotEmpty().MaximumLength(40);
        RuleFor(x => x.TipoDestino).Must(t => t is "cuenta_yuma" or "otra_entidad").WithMessage("tipo_destino debe ser cuenta_yuma u otra_entidad.");
        When(x => x.TipoDestino == "cuenta_yuma", () =>
            RuleFor(x => x.Llave).NotEmpty().Must(l => Cliente.NormalizarLlave(l!) is { Length: 10 } n && n[0] == '3')
                .WithMessage("La llave es un celular colombiano de 10 dígitos que empieza por 3."));
        When(x => x.TipoDestino == "otra_entidad", () =>
        {
            RuleFor(x => x.Entidad).NotEmpty().MaximumLength(64)
                .Must(e => opc.Value.EntidadesReceptoras.Count == 0 || opc.Value.EntidadesReceptoras.Contains(e!))
                .WithMessage("Entidad no reconocida.");
            RuleFor(x => x.NumeroProducto).NotEmpty().Matches("^[0-9]{6,20}$").WithMessage("El número de producto tiene entre 6 y 20 dígitos.");
        });
    }
}

// ---------- Caso de uso ----------
public sealed class ManejadorInscribirDestinatario(BilleteraDbContext db, ServicioAuditoria auditoria, TimeProvider reloj)
{
    public async Task<IResult> EjecutarAsync(InscribirDestinatarioRequest req, ClaimsPrincipal usuario, CancellationToken ct)
    {
        var idCliente = usuario.IdCliente();
        var d = new Destinatario { IdCliente = idCliente, Alias = req.Alias.Trim(), TipoDestino = req.TipoDestino, InscritoUtc = reloj.GetUtcNow().UtcDateTime };
        if (req.TipoDestino == "cuenta_yuma")
        {
            var llave = Cliente.NormalizarLlave(req.Llave!);
            var destino = await db.Clientes.AsNoTracking().FirstOrDefaultAsync(c => c.Llave == llave, ct);
            if (destino is null) return Results.Problem(title: "La llave no está registrada en Yuma Pay.", statusCode: 404);
            if (destino.IdCliente == idCliente) return Results.Problem(title: "No puedes inscribir tu propia cuenta.", statusCode: 422);
            d.IdDestino = destino.IdCliente;
            d.ProductoEnmascarado = MascaraPii.Llave(llave);
        }
        else
        {
            d.Entidad = req.Entidad!.Trim();
            d.IdDestino = IdExterno(d.Entidad, req.NumeroProducto!);
            d.ProductoEnmascarado = "****" + req.NumeroProducto![^4..];
        }
        if (await db.Destinatarios.AnyAsync(x => x.IdCliente == idCliente && x.IdDestino == d.IdDestino, ct))
            return Results.Problem(title: "Ese destinatario ya está inscrito.", statusCode: 409);
        db.Destinatarios.Add(d);
        await db.SaveChangesAsync(ct);
        await auditoria.RegistrarAsync("destinatario_inscrito", "destinatarios/" + d.Id, new { tipo_destino = d.TipoDestino, id_destino = d.IdDestino }, ct: ct);
        return Results.Created($"/v1/destinatarios/{d.Id}", DestinatarioDto.De(d));
    }

    /// <summary>
    /// id_destino estable para un producto de otra entidad ("EXT" + 12 hex de SHA-256 de entidad|número): el Escudo cuenta el fan-in y la
    /// reputación por id_destino, y así el número de producto no viaja en claro.
    /// </summary>
    public static string IdExterno(string entidad, string numero) =>
        "EXT" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entidad.Trim().ToUpperInvariant() + "|" + numero)))[..12];
}

public sealed class InscribirDestinatarioFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddOptions<DestinatariosOptions>().BindConfiguration("Billetera");
        s.AddScoped<IValidator<InscribirDestinatarioRequest>, InscribirDestinatarioValidator>();
        s.AddScoped<ManejadorInscribirDestinatario>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/destinatarios", (InscribirDestinatarioRequest req, ClaimsPrincipal u, ManejadorInscribirDestinatario h, CancellationToken ct) =>
                h.EjecutarAsync(req, u, ct))
            .RequireAuthorization(Politicas.Cliente).ValidarCon<InscribirDestinatarioRequest>().WithTags("Destinatarios");
}
