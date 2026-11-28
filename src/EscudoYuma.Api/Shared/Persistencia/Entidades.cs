using System.Text.Json;

namespace EscudoYuma.Api.Shared.Persistencia;

/// <summary>Roles reconocidos en el claim "rol" del JWT.</summary>
public static class Roles
{
    public const string Cliente = "cliente";
    public const string Analista = "analista";
    public const string Servicio = "servicio";
}

public class Usuario
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdCliente { get; set; } = "";
    /// <summary>Celular o llave de acceso (normalizada: minúsculas, sin espacios).</summary>
    public string Llave { get; set; } = "";
    public string HashClave { get; set; } = "";
    public string Rol { get; set; } = Roles.Cliente;
    public string? Departamento { get; set; }
    public DateOnly? FechaVinculacion { get; set; }
    public List<DispositivoConocido> Dispositivos { get; set; } = new();

    public static string NormalizarLlave(string llave) => llave.Trim().ToLowerInvariant();
}

public class DispositivoConocido
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsuarioId { get; set; }
    public string IdDispositivo { get; set; } = "";
    public string? SistemaOperativo { get; set; }
    public DateTime RegistradoUtc { get; set; }
}

/// <summary>
/// Reputación de una cuenta como RIESGO: 0 = confiable, 100 = riesgo máximo (= 100 - reputación 0-100 de training/features.py).
/// Puntaje = automático (estado en línea, escrito por el escritor en segundo plano) + ajuste del analista (ResolverAlerta).
/// </summary>
public class ReputacionCuenta
{
    public string IdCuenta { get; set; } = "";
    /// <summary>Riesgo calculado por el motor (100 - reputación) tras la última transacción.</summary>
    public int PuntajeAutomatico { get; set; }
    /// <summary>Ajuste acumulado por resoluciones de analistas (fraude confirmado +30, falso positivo -15).</summary>
    public int AjusteAnalista { get; set; }
    public int Puntaje => Math.Clamp(PuntajeAutomatico + AjusteAnalista, 0, 100);
    /// <summary>Señales automáticas vigentes (p. ej. fan-in alto, cuenta reciente, patrón mula); se reemplazan en cada actualización.</summary>
    public List<string> SenalesAutomaticas { get; set; } = new();
    /// <summary>Historial de resoluciones del analista.</summary>
    public List<string> Senales { get; set; } = new();
    public DateTime ActualizadoUtc { get; set; }
    /// <summary>
    /// Bloqueo definitivo. SOLO puede activarse desde ResolverAlerta (analista humano).
    /// Ningún otro componente debe invocar <see cref="MarcarBloqueoDefinitivo"/>.
    /// </summary>
    public bool BloqueoDefinitivo { get; private set; } // setter privado: solo MarcarBloqueoDefinitivo lo activa
    public string? BloqueadaPor { get; private set; }

    public void AgregarSenal(string senal)
    {
        Senales.Add(senal);
        if (Senales.Count > 50) Senales.RemoveRange(0, Senales.Count - 50); // se conservan las 50 más recientes
    }

    /// <summary>Ajuste del analista (no se pisa con las actualizaciones automáticas).</summary>
    public void AjustarPuntaje(int delta, DateTime ahoraUtc)
    {
        AjusteAnalista = Math.Clamp(AjusteAnalista + delta, -100, 100);
        ActualizadoUtc = ahoraUtc;
    }

    /// <summary>Actualización automática: reputación 0-100 del motor (100 = confiable) -> riesgo 0-100.</summary>
    public void ActualizarAutomatica(double reputacion, IReadOnlyList<string> senales, DateTime ahoraUtc)
    {
        PuntajeAutomatico = (int)Math.Round(100 - Math.Clamp(reputacion, 0, 100));
        SenalesAutomaticas = senales.ToList();
        ActualizadoUtc = ahoraUtc;
    }

    /// <summary>Único punto de escritura del bloqueo definitivo (usado solo por ResolverAlerta).</summary>
    public void MarcarBloqueoDefinitivo(string analista, DateTime ahoraUtc)
    {
        BloqueoDefinitivo = true;
        BloqueadaPor = analista;
        ActualizadoUtc = ahoraUtc;
    }
}

public static class EstadosAlerta
{
    public const string Pendiente = "pendiente";
    public const string Resuelta = "resuelta";
}

public class Alerta
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdTransaccion { get; set; } = "";
    public string IdCliente { get; set; } = "";
    public long MontoCop { get; set; }
    public double Puntaje { get; set; }
    /// <summary>Decisión externa (retener | rechazar).</summary>
    public string Decision { get; set; } = "";
    public List<string> Razones { get; set; } = new();
    public string Estado { get; set; } = EstadosAlerta.Pendiente;
    public string? Analista { get; set; }
    /// <summary>fraude_confirmado | falso_positivo</summary>
    public string? Resolucion { get; set; }
    public string? Accion { get; set; }
    public string? Comentario { get; set; }
    public DateTime CreadaUtc { get; set; }
    public DateTime? ResueltaUtc { get; set; }
}

/// <summary>
/// Hecho del expediente (append-only): todo lo relevante para explicar una decisión, con esquema fijo y enlazado a la cadena de auditoría.
/// Es la fuente ordenada que se narra con el LLM: el modelo de lenguaje nunca decide, solo describe estos hechos.
/// Sin datos personales en claro (ids seudónimos, llaves enmascaradas).
/// </summary>
public class HechoExpediente
{
    public long Id { get; set; }
    /// <summary>transaccion | decision | evento_app | ingreso | alerta | desafio_fallido | resolucion | cambio_configuracion | alteracion_auditoria</summary>
    public string Tipo { get; set; } = "";
    public DateTime FechaUtc { get; set; }
    public string? IdCliente { get; set; }
    public string? IdTransaccion { get; set; }
    public string IdCorrelacion { get; set; } = "";
    /// <summary>billetera | escudo | analista | sistema</summary>
    public string Fuente { get; set; } = "";
    /// <summary>JSON con los datos del hecho (cifras concretas: montos, señales 0/1, puntaje, contribuciones).</summary>
    public string DatosJson { get; set; } = "{}";
    /// <summary>Códigos de normas aplicadas (R001-R012, POL-*, PISO-*, NORMA-*): ver CatalogoNormas.</summary>
    public List<string> Normas { get; set; } = new();
    public long? SecuenciaAuditoria { get; set; }
    public string? HashAuditoria { get; set; }
}

/// <summary>Narrativa en lenguaje natural de una alerta (o de la actividad de un cliente), generada a partir de su expediente.</summary>
public class NarrativaAlerta
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IdAlerta { get; set; }
    /// <summary>JSON validado: resumen, linea_de_tiempo, factores, normas_aplicadas, recomendacion_al_analista, confianza.</summary>
    public string ContenidoJson { get; set; } = "{}";
    /// <summary>ollama | plantilla (respaldo determinista cuando el LLM no está o su salida no pasa la validación).</summary>
    public string Origen { get; set; } = "";
    public string Modelo { get; set; } = "";
    public string VersionPrompt { get; set; } = "";
    /// <summary>SHA-256 del expediente narrado: permite saber si la narrativa quedó desactualizada.</summary>
    public string HashExpediente { get; set; } = "";
    public double LatenciaMs { get; set; }
    public string? MotivoRespaldo { get; set; }
    public DateTime CreadaUtc { get; set; }
}

internal static class JsonLista
{
    public static string Serializar(List<string> l) => JsonSerializer.Serialize(l);
    public static List<string> Deserializar(string s) => JsonSerializer.Deserialize<List<string>>(s) ?? new();
}
