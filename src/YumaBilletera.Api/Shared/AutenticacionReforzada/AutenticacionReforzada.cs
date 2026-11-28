using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using YumaBilletera.Api.Shared.Persistencia;

namespace YumaBilletera.Api.Shared.AutenticacionReforzada;

public sealed class VerificacionOptions
{
    public int MinutosVigencia { get; set; } = 5;
    public int MaxIntentos { get; set; } = 3;
}

/// <summary>Puerto de envío del OTP (SMS). Sin proveedor real configurado no se envía nada y el ingreso con OTP falla cerrado.</summary>
public interface IEnviadorOtp
{
    Task EnviarAsync(string idCliente, Guid idVerificacion, string codigo, CancellationToken ct);
}

public sealed class EnviadorOtpNoConfigurado : IEnviadorOtp
{
    public Task EnviarAsync(string idCliente, Guid idVerificacion, string codigo, CancellationToken ct) =>
        throw new InvalidOperationException("No hay proveedor de SMS configurado para el OTP.");
}

/// <summary>
/// Buzón de OTP SOLO para Development: guarda el último código de cada verificación en memoria para que la app de pruebas lo lea
/// (<c>GET /v1/dev/otp/{id}</c>). Nunca se registra fuera de Development.
/// </summary>
public sealed class BuzonOtpDesarrollo : IEnviadorOtp
{
    private readonly ConcurrentDictionary<Guid, string> _codigos = new();
    public Task EnviarAsync(string idCliente, Guid idVerificacion, string codigo, CancellationToken ct)
    {
        _codigos[idVerificacion] = codigo;
        return Task.CompletedTask;
    }
    public string? Leer(Guid idVerificacion) => _codigos.TryGetValue(idVerificacion, out var c) ? c : null;
}

/// <summary>Resultado que entrega el proveedor de biometría facial (prueba de vida + comparación con el rostro enrolado).</summary>
public sealed record ResultadoBiometria(bool Supera, string Detalle);

/// <summary>Puerto de biometría facial (Adapter). El caso pide "autenticación reforzada: biometría facial en la app" para riesgo medio.</summary>
public interface IVerificadorBiometrico
{
    Task<ResultadoBiometria> VerificarAsync(string idCliente, string resultadoSimulado, CancellationToken ct);
}

/// <summary>
/// Biometría SIMULADA (no hay proveedor real en el alcance del reto): la app de demostración indica si la captura "supera" o "no_supera".
/// Se reemplaza registrando otro <see cref="IVerificadorBiometrico"/> sin cambiar los casos de uso.
/// </summary>
public sealed class BiometriaSimulada : IVerificadorBiometrico
{
    public const string Supera = "supera";
    public const string NoSupera = "no_supera";
    public Task<ResultadoBiometria> VerificarAsync(string idCliente, string resultadoSimulado, CancellationToken ct) =>
        Task.FromResult(resultadoSimulado == Supera
            ? new ResultadoBiometria(true, "Rostro verificado (simulación).")
            : new ResultadoBiometria(false, "El rostro no coincide (simulación)."));
}

public enum EstadoIntento { Valido, Invalido, Agotada, Expirada, NoEncontrada }

/// <summary>Crea y valida verificaciones reforzadas (OTP con hash SHA-256, vigencia e intentos acotados). Scoped.</summary>
public sealed class ServicioVerificacion(BilleteraDbContext db, IEnviadorOtp enviador, IVerificadorBiometrico biometria, TimeProvider reloj,
    Microsoft.Extensions.Options.IOptions<VerificacionOptions> opciones)
{
    public async Task<Persistencia.Verificacion> CrearAsync(string proposito, string metodo, string referencia, string idCliente, CancellationToken ct)
    {
        var v = new Persistencia.Verificacion
        {
            Proposito = proposito, Metodo = metodo, Referencia = referencia, IdCliente = idCliente,
            ExpiraUtc = reloj.GetUtcNow().UtcDateTime.AddMinutes(opciones.Value.MinutosVigencia),
        };
        if (metodo == MetodosVerificacion.Otp)
        {
            var codigo = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            v.HashCodigo = Hash(codigo);
            await enviador.EnviarAsync(idCliente, v.Id, codigo, ct);
        }
        db.Verificaciones.Add(v);
        return v;
    }

    /// <summary>Busca una verificación pendiente del cliente y propósito dados (el cliente solo puede usar las suyas).</summary>
    public Task<Persistencia.Verificacion?> BuscarAsync(Guid id, string idCliente, string proposito, CancellationToken ct) =>
        db.Verificaciones.FirstOrDefaultAsync(v => v.Id == id && v.IdCliente == idCliente && v.Proposito == proposito, ct);

    /// <summary>Valida un OTP. Cuenta el intento aunque falle; al agotar intentos la verificación queda inutilizable.</summary>
    public EstadoIntento ValidarOtp(Persistencia.Verificacion v, string codigo)
    {
        var previo = Previo(v);
        if (previo is not null) return previo.Value;
        if (v.Metodo != MetodosVerificacion.Otp) return EstadoIntento.NoEncontrada;
        v.Intentos++;
        var ok = v.HashCodigo is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(v.HashCodigo), Encoding.ASCII.GetBytes(Hash(codigo)));
        if (ok) { v.Resuelta = true; return EstadoIntento.Valido; }
        return v.Intentos >= opciones.Value.MaxIntentos ? EstadoIntento.Agotada : EstadoIntento.Invalido;
    }

    /// <summary>Valida la biometría. Un "no supera" agota la verificación (va a analista): no se permite reintentar hasta acertar.</summary>
    public async Task<(EstadoIntento Estado, string Detalle)> ValidarBiometriaAsync(Persistencia.Verificacion v, string resultadoSimulado, CancellationToken ct)
    {
        var previo = Previo(v);
        if (previo is not null) return (previo.Value, "");
        if (v.Metodo != MetodosVerificacion.Biometria) return (EstadoIntento.NoEncontrada, "");
        v.Intentos++;
        var r = await biometria.VerificarAsync(v.IdCliente, resultadoSimulado, ct);
        v.Resuelta = true; // un solo intento: si no supera, el caso pasa al analista
        return (r.Supera ? EstadoIntento.Valido : EstadoIntento.Agotada, r.Detalle);
    }

    private EstadoIntento? Previo(Persistencia.Verificacion v)
    {
        if (v.Resuelta) return EstadoIntento.Agotada;
        if (reloj.GetUtcNow().UtcDateTime > v.ExpiraUtc) return EstadoIntento.Expirada;
        if (v.Intentos >= opciones.Value.MaxIntentos) return EstadoIntento.Agotada;
        return null;
    }

    private static string Hash(string codigo) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(codigo))).ToLowerInvariant();
}
