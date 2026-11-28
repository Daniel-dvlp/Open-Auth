using System.Security.Cryptography;
using System.Text;
using YumaBilletera.Api.Shared.Escudo;
using YumaBilletera.Api.Shared.Persistencia;

namespace YumaBilletera.Api.Shared.Sesiones;

public sealed class SesionOptions
{
    /// <summary>Duración máxima de la sesión (refresh token). El token de acceso dura Jwt:MinutosVigencia (15 min).</summary>
    public int HorasRefresh { get; set; } = 8;
}

/// <summary>Tokens que recibe la app al completar el ingreso.</summary>
public sealed record TokensSesion(string Token, DateTime ExpiraUtc, string RefreshToken, DateTime RefreshExpiraUtc);

/// <summary>
/// Activa sesiones y emite tokens (acceso JWT 15 min + refresh rotativo de un solo uso, guardado como hash). Al activar una sesión
/// se avisa al Escudo del ingreso exitoso (evento inicio_sesion), igual que hace el login del Escudo. Scoped.
/// </summary>
public sealed class EmisorSesion(BilleteraDbContext db, JwtEmisor jwt, PublicadorEventos publicador, TimeProvider reloj,
    Microsoft.Extensions.Options.IOptions<SesionOptions> opciones)
{
    /// <summary>Activa la sesión, encola el evento y devuelve tokens. El llamador guarda.</summary>
    public TokensSesion Activar(SesionCliente sesion)
    {
        sesion.Estado = EstadosSesion.Activa;
        publicador.Encolar(sesion.IdCliente, TiposEventoApp.InicioSesion, sesion.IdDispositivo, null, sesion.DepartamentoIp, exitoso: true);
        return Rotar(sesion);
    }

    /// <summary>Emite un nuevo par de tokens e invalida el refresh anterior (rotación). El llamador guarda.</summary>
    public TokensSesion Rotar(SesionCliente sesion)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var emitido = jwt.Emitir(sesion.IdCliente, Roles.Cliente, sesion.IdSesion, sesion.ScoreIngreso, reloj);
        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        sesion.HashRefresh = Hash(refresh);
        sesion.RefreshExpiraUtc ??= ahora.AddHours(opciones.Value.HorasRefresh); // la sesión no se alarga al rotar
        sesion.ExpiraUtc = sesion.RefreshExpiraUtc.Value;
        return new TokensSesion(emitido.Token, emitido.ExpiraUtc, sesion.IdSesion + "." + refresh, sesion.RefreshExpiraUtc.Value);
    }

    /// <summary>Busca la sesión de un refresh token válido (formato "idSesion.secreto"), comparando el hash en tiempo constante.</summary>
    public async Task<SesionCliente?> BuscarPorRefreshAsync(string refreshToken, CancellationToken ct)
    {
        var partes = refreshToken.Split('.', 2);
        if (partes.Length != 2) return null;
        var sesion = await db.Sesiones.FirstOrDefaultAsync(s => s.IdSesion == partes[0], ct);
        if (sesion?.HashRefresh is null || sesion.Estado != EstadosSesion.Activa || sesion.RefreshExpiraUtc < reloj.GetUtcNow().UtcDateTime) return null;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sesion.HashRefresh), Encoding.ASCII.GetBytes(Hash(partes[1]))) ? sesion : null;
    }

    private static string Hash(string valor) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valor))).ToLowerInvariant();
}
