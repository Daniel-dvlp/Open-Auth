using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Yuma.Comun.Seguridad;

public sealed class JwtOptions
{
    /// <summary>Secreto HMAC (>= 32 caracteres). En producción SOLO por variable de entorno Jwt__Clave / secretos.</summary>
    public string? Clave { get; set; }
    public string Emisor { get; set; } = "escudo-yuma";
    public string Audiencia { get; set; } = "escudo-yuma-clientes";
    public int MinutosVigencia { get; set; } = 15;
}

public sealed record TokenEmitido(string Token, DateTime ExpiraUtc);

/// <summary>
/// Singleton: la clave de firma se deriva una sola vez. En Development, si no hay clave configurada, se genera una
/// efímera por proceso (los tokens dejan de valer al reiniciar). Fuera de Development, la clave es obligatoria.
/// </summary>
public sealed class JwtEmisor
{
    private readonly JwtOptions _opc;
    public SymmetricSecurityKey LlaveFirma { get; }

    public JwtEmisor(IOptions<JwtOptions> opc, IHostEnvironment env, ILogger<JwtEmisor> log)
    {
        _opc = opc.Value;
        var clave = _opc.Clave;
        if (string.IsNullOrWhiteSpace(clave))
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException("Falta el secreto JWT (variable de entorno Jwt__Clave).");
            clave = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            log.LogWarning("Jwt:Clave no configurada: se usa una clave efímera de desarrollo.");
        }
        if (clave.Length < 32) throw new InvalidOperationException("Jwt:Clave debe tener al menos 32 caracteres.");
        // Fuera de Development se rechazan claves triviales (p. ej. "aaaa…" o un valor de ejemplo repetido): HMAC-SHA256 es tan fuerte como su secreto.
        if (!env.IsDevelopment() && clave.Distinct().Count() < 12)
            throw new InvalidOperationException("Jwt:Clave es demasiado predecible: use un secreto aleatorio (p. ej. 48 bytes en base64).");
        LlaveFirma = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(clave));
    }

    public TokenEmitido Emitir(string idCliente, string rol, string idSesion, double scoreIngreso, TimeProvider reloj)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var expira = ahora.AddMinutes(_opc.MinutosVigencia);
        var claims = new[]
        {
            new Claim("sub", idCliente),
            new Claim("sid", idSesion),
            new Claim("rol", rol),
            new Claim("score_ingreso", scoreIngreso.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Double),
            new Claim("jti", Guid.NewGuid().ToString("N")),
        };
        var jwt = new JwtSecurityToken(_opc.Emisor, _opc.Audiencia, claims, ahora, expira,
            new SigningCredentials(LlaveFirma, SecurityAlgorithms.HmacSha256));
        return new TokenEmitido(new JwtSecurityTokenHandler().WriteToken(jwt), expira);
    }
}

/// <summary>Rango admitido para fecha_hora de transacciones y eventos (hora local de Colombia, UTC-5).</summary>
public static class RangoFecha
{
    public static readonly DateTime Minima = new(2020, 1, 1);
    public static readonly TimeSpan ToleranciaFuturo = TimeSpan.FromDays(1);

    public static bool EsValida(string? fechaHora, DateTime ahoraUtc) =>
        DateTime.TryParseExact(fechaHora, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var f)
        && f >= Minima && f <= ahoraUtc.AddHours(-5) + ToleranciaFuturo;
}

/// <summary>Enmascara datos personales antes de loguear/auditar.</summary>
public static class MascaraPii
{
    public static string Llave(string? llave)
    {
        if (string.IsNullOrEmpty(llave)) return "***";
        var l = llave.Trim();
        return l.Length <= 6 ? new string('*', l.Length) : l[..3] + new string('*', l.Length - 6) + l[^3..];
    }
}

/// <summary>
/// Singleton: limitador de ventana deslizante en memoria (por llave e IP). Con varias réplicas debería ir a Redis.
/// La tabla está acotada (<paramref name="maxClaves"/>): un atacante que envíe llaves arbitrarias no puede agotar la memoria ni volver
/// cuadrática la purga. Si la tabla se satura con ventanas vigentes se deniega (falla cerrado) en vez de dejar de limitar.
/// </summary>
public sealed class LimitadorVentana(TimeProvider reloj, int maxClaves = 200_000)
{
    private const int UmbralPurga = 20_000;
    private readonly Dictionary<string, Queue<DateTime>> _eventos = new();
    private readonly object _lock = new();
    private DateTime _ultimaPurga = DateTime.MinValue;

    /// <summary>Registra un intento; devuelve false (y el tiempo de espera) si se superó el máximo en la ventana.</summary>
    public bool Intentar(string clave, int max, TimeSpan ventana, out TimeSpan reintentarEn)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            // La purga recorre toda la tabla: se hace a lo sumo una vez por segundo, no en cada petición.
            if ((_eventos.Count > UmbralPurga || _eventos.Count >= maxClaves) && ahora - _ultimaPurga >= TimeSpan.FromSeconds(1)) { Purgar(ahora, ventana); _ultimaPurga = ahora; }
            if (!_eventos.TryGetValue(clave, out var cola))
            {
                if (_eventos.Count >= maxClaves) { reintentarEn = ventana; return false; }
                _eventos[clave] = cola = new Queue<DateTime>();
            }
            while (cola.Count > 0 && ahora - cola.Peek() >= ventana) cola.Dequeue();
            if (cola.Count >= max)
            {
                reintentarEn = ventana - (ahora - cola.Peek());
                return false;
            }
            cola.Enqueue(ahora);
            reintentarEn = TimeSpan.Zero;
            return true;
        }
    }

    private void Purgar(DateTime ahora, TimeSpan ventana)
    {
        foreach (var k in _eventos.Where(kv => kv.Value.Count == 0 || ahora - kv.Value.Peek() >= ventana).Select(kv => kv.Key).ToList())
            _eventos.Remove(k);
    }
}

