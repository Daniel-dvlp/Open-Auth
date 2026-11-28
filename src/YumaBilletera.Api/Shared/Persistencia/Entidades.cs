using System.Text.Json;

namespace YumaBilletera.Api.Shared.Persistencia;

/// <summary>Roles del claim "rol" en los tokens que emite la billetera.</summary>
public static class Roles
{
    public const string Cliente = "cliente";
}

/// <summary>
/// Cliente de la billetera. Se siembra desde <c>clientes.csv</c> del caso. No se guardan rango de edad ni la etiqueta
/// <c>cuenta_mula_confirmada</c>: la billetera no los necesita para operar y la decisión de riesgo no puede discriminar por edad
/// (restricción del caso); la etiqueta de mula es información del futuro.
/// </summary>
public class Cliente
{
    /// <summary>Identificador del caso (p. ej. C01234). También es el id de su cuenta y el id_destino que ve el Escudo.</summary>
    public string IdCliente { get; set; } = "";
    /// <summary>Llave de transferencias inmediatas: celular sintético de 10 dígitos (mismo esquema del seed del Escudo).</summary>
    public string Llave { get; set; } = "";
    public string HashClave { get; set; } = "";
    /// <summary>persona | microcomerciante</summary>
    public string TipoCliente { get; set; } = "persona";
    /// <summary>Departamento habitual (clientes.csv). Se compara con el departamento de la IP.</summary>
    public string Departamento { get; set; } = "";
    public DateOnly FechaVinculacion { get; set; }
    /// <summary>app | corresponsal | referido</summary>
    public string CanalVinculacion { get; set; } = "app";

    public static string NormalizarLlave(string llave) => new(llave.Where(char.IsDigit).ToArray());
}

public static class TiposCuenta
{
    /// <summary>Depósito electrónico de un cliente.</summary>
    public const string Cliente = "cliente";
    /// <summary>Cuenta de compensación hacia un tipo de destino externo (otra entidad, comercio, servicio, operador).</summary>
    public const string Puente = "puente";
    /// <summary>Contrapartida de los saldos iniciales sembrados (puede quedar negativa).</summary>
    public const string Apertura = "apertura";
}

public static class EstadosCuenta
{
    public const string Activa = "activa";
    /// <summary>Congelada preventivamente (no puede enviar). La puede levantar un analista.</summary>
    public const string Congelada = "congelada";
    /// <summary>Bloqueo definitivo: SOLO por decisión de un analista en el Escudo (restricción del caso).</summary>
    public const string Bloqueada = "bloqueada";
}

/// <summary>
/// Cuenta del libro mayor. Montos en pesos colombianos enteros (long): nunca double en dinero.
/// <see cref="SaldoCop"/> es el saldo contable; <see cref="ReservadoCop"/> lo retenido por transferencias en evaluación o revisión.
/// </summary>
public class Cuenta
{
    public string IdCuenta { get; set; } = "";
    public string? IdCliente { get; set; }
    public string Tipo { get; set; } = TiposCuenta.Cliente;
    public long SaldoCop { get; set; }
    public long ReservadoCop { get; set; }
    public string Estado { get; set; } = EstadosCuenta.Activa;
    public DateTime CreadaUtc { get; set; }
    /// <summary>Token de concurrencia optimista (funciona igual en Postgres y en memoria): cada cambio de saldo lo renueva.</summary>
    public Guid Version { get; set; } = Guid.NewGuid();

    public long DisponibleCop => SaldoCop - ReservadoCop;
    public bool PuedeQuedarNegativa => Tipo == TiposCuenta.Apertura;
}

public static class TiposMovimiento
{
    public const string Debito = "debito";
    public const string Credito = "credito";
}

/// <summary>Línea del libro mayor (append-only). Cada asiento tiene exactamente un débito y un crédito por el mismo monto.</summary>
public class Movimiento
{
    public long Id { get; set; }
    public Guid IdAsiento { get; set; }
    public string IdCuenta { get; set; } = "";
    public string Tipo { get; set; } = "";
    public long MontoCop { get; set; }
    public long SaldoDespuesCop { get; set; }
    /// <summary>apertura | transferencia | pago_qr | pago_servicios | recarga_celular | reverso</summary>
    public string Concepto { get; set; } = "";
    public string? IdTransaccion { get; set; }
    /// <summary>Cuenta del otro lado del asiento (para mostrar el historial sin otra consulta).</summary>
    public string Contraparte { get; set; } = "";
    public DateTime FechaUtc { get; set; }
}

public static class EstadosTransferencia
{
    /// <summary>Recibida, monto reservado, esperando la decisión del Escudo.</summary>
    public const string Evaluando = "evaluando";
    public const string Completada = "completada";
    /// <summary>Riesgo medio: espera la autenticación reforzada (biometría facial).</summary>
    public const string RequiereVerificacion = "requiere_verificacion";
    /// <summary>Retenida para un analista (o el Escudo no respondió: falla cerrada). El monto sigue reservado.</summary>
    public const string EnRevision = "en_revision";
    public const string Rechazada = "rechazada";
}

/// <summary>Operación que mueve dinero hacia afuera de la cuenta del cliente. Su id es el mismo id_transaccion que viaja al Escudo.</summary>
public class Transferencia
{
    public string IdTransaccion { get; set; } = "";
    public string IdClienteOrigen { get; set; } = "";
    /// <summary>transferencia_inmediata | pago_qr | pago_servicios | recarga_celular (catálogo de transacciones.csv).</summary>
    public string TipoOperacion { get; set; } = "";
    public string Canal { get; set; } = "app";
    public string IdDestino { get; set; } = "";
    /// <summary>cuenta_yuma | otra_entidad | comercio | servicio | operador</summary>
    public string TipoDestino { get; set; } = "";
    /// <summary>Cuenta del libro mayor que recibe el crédito (otro cliente Yuma o la cuenta puente del tipo de destino).</summary>
    public string IdCuentaCredito { get; set; } = "";
    public long MontoCop { get; set; }
    public long SaldoAntesCop { get; set; }
    public string? Descripcion { get; set; }
    public string Estado { get; set; } = EstadosTransferencia.Evaluando;
    public string ClaveIdempotencia { get; set; } = "";

    // Contexto de riesgo enviado al Escudo (instantánea, para auditoría y para el expediente).
    public string IdSesion { get; set; } = "";
    public string IdDispositivo { get; set; } = "";
    public string DepartamentoIp { get; set; } = "";
    public int DestinoNuevo { get; set; }
    public int DispositivoNuevo { get; set; }
    public int CambioClave24h { get; set; }
    public int CambioSim48h { get; set; }
    public int IpDepartamentoDistinto { get; set; }
    public int AntiguedadCuentaDias { get; set; }

    // Respuesta del Escudo.
    public string? Decision { get; set; }
    public string? DecisionInterna { get; set; }
    public double? Puntaje { get; set; }
    public List<string> Razones { get; set; } = new();
    public string? VersionModelo { get; set; }
    /// <summary>Mensaje para el cliente (sin revelar reglas ni umbrales: el caso prohíbe publicarlos).</summary>
    public string? MensajeCliente { get; set; }

    public DateTime CreadaUtc { get; set; }
    public DateTime? ResueltaUtc { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}

/// <summary>Libreta de destinatarios del cliente.</summary>
public class Destinatario
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdCliente { get; set; } = "";
    public string Alias { get; set; } = "";
    public string TipoDestino { get; set; } = "";
    public string IdDestino { get; set; } = "";
    /// <summary>Solo para otra_entidad: entidad financiera receptora (catálogo configurable).</summary>
    public string? Entidad { get; set; }
    /// <summary>Solo para otra_entidad: número de producto enmascarado para mostrar.</summary>
    public string? ProductoEnmascarado { get; set; }
    public DateTime InscritoUtc { get; set; }
}

/// <summary>Última operación del cliente hacia un destino: alimenta <c>destino_nuevo</c> (sin operaciones en 180 días).</summary>
public class HistorialDestino
{
    public string IdCliente { get; set; } = "";
    public string IdDestino { get; set; } = "";
    public DateTime UltimaOperacionUtc { get; set; }
}

/// <summary>Dispositivo registrado por el cliente (alimenta <c>dispositivo_nuevo</c>: registrado en las últimas 24 h).</summary>
public class Dispositivo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdCliente { get; set; } = "";
    public string IdDispositivo { get; set; } = "";
    public string? SistemaOperativo { get; set; }
    public DateTime RegistradoUtc { get; set; }
}

public static class TiposEventoSeguridad
{
    public const string CambioClave = "cambio_clave";
    public const string CambioSim = "cambio_sim_detectado";
}

/// <summary>Cambios de clave y de SIM del cliente (alimentan <c>cambio_clave_24h</c> y <c>cambio_sim_48h</c>).</summary>
public class EventoSeguridad
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdCliente { get; set; } = "";
    public string Tipo { get; set; } = "";
    public DateTime FechaUtc { get; set; }
}

public static class EstadosSesion
{
    public const string Activa = "activa";
    public const string PendienteOtp = "pendiente_otp";
    public const string PendienteBiometria = "pendiente_biometria";
    public const string Cerrada = "cerrada";
}

/// <summary>Sesión de la app: guarda dispositivo y departamento de la IP del ingreso para calificar las operaciones de esa sesión.</summary>
public class SesionCliente
{
    public string IdSesion { get; set; } = "";
    public string IdCliente { get; set; } = "";
    public string IdDispositivo { get; set; } = "";
    public string DepartamentoIp { get; set; } = "";
    public double ScoreIngreso { get; set; }
    public string Estado { get; set; } = EstadosSesion.Activa;
    public string? HashRefresh { get; set; }
    public DateTime? RefreshExpiraUtc { get; set; }
    public DateTime CreadaUtc { get; set; }
    public DateTime ExpiraUtc { get; set; }
}

public static class PropositosVerificacion
{
    public const string Ingreso = "ingreso";
    public const string Transferencia = "transferencia";
}

public static class MetodosVerificacion
{
    public const string Otp = "otp";
    public const string Biometria = "biometria";
}

/// <summary>
/// Autenticación reforzada pendiente (paso adicional). Transferencias de riesgo medio: biometría facial. Ingreso con OTP del Escudo:
/// código de un solo uso, salvo que haya cambio de SIM en 48 h, en cuyo caso se exige biometría (decisión del 2026-11-27).
/// </summary>
public class Verificacion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Proposito { get; set; } = "";
    public string Metodo { get; set; } = "";
    /// <summary>Id de la sesión (ingreso) o id_transaccion (transferencia).</summary>
    public string Referencia { get; set; } = "";
    public string IdCliente { get; set; } = "";
    /// <summary>Hash SHA-256 del OTP (nunca el código en claro).</summary>
    public string? HashCodigo { get; set; }
    public int Intentos { get; set; }
    public bool Resuelta { get; set; }
    public DateTime ExpiraUtc { get; set; }
}

/// <summary>Bandeja de salida (outbox): eventos que deben llegar al Escudo aunque esté caído en el momento.</summary>
public class EventoSaliente
{
    public long Id { get; set; }
    /// <summary>evento_app | desafio_fallido</summary>
    public string Tipo { get; set; } = "";
    public string CargaJson { get; set; } = "{}";
    public DateTime CreadoUtc { get; set; }
    public DateTime? EnviadoUtc { get; set; }
    public int Intentos { get; set; }
}

internal static class JsonLista
{
    public static string Serializar(List<string> l) => JsonSerializer.Serialize(l);
    public static List<string> Deserializar(string s) => JsonSerializer.Deserialize<List<string>>(s) ?? new();
}
