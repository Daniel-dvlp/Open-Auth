using System.Text.Json;
using System.Text.Json.Serialization;
using YumaBilletera.Api.Shared.Persistencia;

namespace YumaBilletera.Api.Shared.Escudo;

/// <summary>
/// Esquema <c>Transaccion</c> del Escudo (openapi del sandbox, mismos nombres que transacciones.csv). Nombres JSON explícitos: la política
/// snake_case convertiría CambioClave24h en "cambio_clave24h".
/// </summary>
public sealed record TransaccionEscudo(
    [property: JsonPropertyName("id_transaccion")] string IdTransaccion,
    [property: JsonPropertyName("fecha_hora")] string FechaHora,
    [property: JsonPropertyName("id_cliente")] string IdCliente,
    [property: JsonPropertyName("tipo_operacion")] string TipoOperacion,
    [property: JsonPropertyName("canal")] string Canal,
    [property: JsonPropertyName("monto_cop")] long MontoCop,
    [property: JsonPropertyName("saldo_antes_cop")] long SaldoAntesCop,
    [property: JsonPropertyName("id_destino")] string IdDestino,
    [property: JsonPropertyName("tipo_destino")] string TipoDestino,
    [property: JsonPropertyName("destino_nuevo")] int DestinoNuevo,
    [property: JsonPropertyName("id_dispositivo")] string IdDispositivo,
    [property: JsonPropertyName("dispositivo_nuevo")] int DispositivoNuevo,
    [property: JsonPropertyName("cambio_clave_24h")] int CambioClave24h,
    [property: JsonPropertyName("cambio_sim_48h")] int CambioSim48h,
    [property: JsonPropertyName("departamento_ip")] string DepartamentoIp,
    [property: JsonPropertyName("ip_departamento_distinto")] int IpDepartamentoDistinto,
    [property: JsonPropertyName("antiguedad_cuenta_dias")] int AntiguedadCuentaDias);

/// <summary>Respuesta de <c>POST /v1/score/transaccion</c>.</summary>
public sealed record DecisionEscudo(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("decision_interna")] string DecisionInterna,
    [property: JsonPropertyName("puntaje")] double Puntaje,
    [property: JsonPropertyName("razones")] IReadOnlyList<string> Razones,
    [property: JsonPropertyName("version_modelo")] string VersionModelo);

public static class DecisionesEscudo
{
    public const string Aprobar = "aprobar";
    public const string Desafiar = "desafiar";
    public const string Retener = "retener";
    public const string Rechazar = "rechazar";
    /// <summary>Nivel interno de desafío con mensaje anti-ingeniería social (destino nuevo y monto alto frente al saldo).</summary>
    public const string Advertir = "advertir";
}

/// <summary>Contexto del ingreso que la billetera envía para el score de ingreso (la clave ya fue validada por la billetera).</summary>
public sealed record IngresoEscudo(
    [property: JsonPropertyName("id_cliente")] string IdCliente,
    [property: JsonPropertyName("id_dispositivo")] string IdDispositivo,
    [property: JsonPropertyName("departamento_ip")] string DepartamentoIp,
    [property: JsonPropertyName("dispositivos_conocidos")] IReadOnlyList<string> DispositivosConocidos);

/// <summary>Resultado del score de ingreso: permitir | otp | bloqueo_temporal.</summary>
public sealed record ResultadoIngresoEscudo(
    [property: JsonPropertyName("resultado")] string Resultado,
    [property: JsonPropertyName("score_ingreso")] double ScoreIngreso,
    [property: JsonPropertyName("razones")] IReadOnlyList<string> Razones);

public static class ResultadosIngreso
{
    public const string Permitir = "permitir";
    public const string Otp = "otp";
    public const string BloqueoTemporal = "bloqueo_temporal";
}

/// <summary>Evento de la app con el esquema de eventos_app.csv (lo ingiere <c>POST /v1/eventos</c> del Escudo).</summary>
public sealed record EventoApp(
    [property: JsonPropertyName("id_evento")] string IdEvento,
    [property: JsonPropertyName("fecha_hora")] string FechaHora,
    [property: JsonPropertyName("id_cliente")] string IdCliente,
    [property: JsonPropertyName("tipo_evento")] string TipoEvento,
    [property: JsonPropertyName("id_dispositivo")] string? IdDispositivo,
    [property: JsonPropertyName("sistema_operativo")] string? SistemaOperativo,
    [property: JsonPropertyName("departamento_ip")] string? DepartamentoIp,
    [property: JsonPropertyName("resultado")] string Resultado);

public static class TiposEventoApp
{
    public const string InicioSesion = "inicio_sesion";
    public const string RegistroDispositivo = "registro_dispositivo";
    public const string CambioClave = "cambio_clave";
    public const string CambioSim = "cambio_sim_detectado";
}

/// <summary>El Escudo no respondió a tiempo o respondió con error. La billetera falla cerrado: el dinero no sale.</summary>
public sealed class EscudoNoDisponibleException(string mensaje, Exception? interna = null) : Exception(mensaje, interna);

/// <summary>
/// Puerto hacia el Escudo Yuma (Adapter). La implementación HTTP (typed HttpClient con token de servicio) se registra en la integración;
/// sin ella, <see cref="EscudoNoConfigurado"/> hace que toda operación quede en revisión (falla cerrada, nunca aprueba por defecto).
/// </summary>
public interface IEscudoCliente
{
    Task<DecisionEscudo> EvaluarTransaccionAsync(TransaccionEscudo tx, CancellationToken ct);
    Task<ResultadoIngresoEscudo> EvaluarIngresoAsync(IngresoEscudo ingreso, CancellationToken ct);
}

/// <summary>Implementación por defecto cuando no hay Escudo configurado: siempre "no disponible" (falla cerrada).</summary>
public sealed class EscudoNoConfigurado : IEscudoCliente
{
    public Task<DecisionEscudo> EvaluarTransaccionAsync(TransaccionEscudo tx, CancellationToken ct) =>
        throw new EscudoNoDisponibleException("Escudo no configurado (Escudo:UrlBase).");
    public Task<ResultadoIngresoEscudo> EvaluarIngresoAsync(IngresoEscudo ingreso, CancellationToken ct) =>
        throw new EscudoNoDisponibleException("Escudo no configurado (Escudo:UrlBase).");
}

/// <summary>
/// Publica eventos hacia el Escudo con bandeja de salida (outbox): el evento se guarda en la misma unidad de trabajo que el cambio
/// que lo origina y un proceso en segundo plano lo entrega, así un Escudo caído no pierde eventos. Scoped.
/// </summary>
public sealed class PublicadorEventos(BilleteraDbContext db, TimeProvider reloj)
{
    public const string TipoEventoApp = "evento_app";

    /// <summary>Encola un evento de la app; se guarda con el SaveChanges del llamador.</summary>
    public EventoApp Encolar(string idCliente, string tipoEvento, string? idDispositivo, string? sistemaOperativo, string? departamentoIp, bool exitoso)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var ev = new EventoApp("BIL-" + Guid.NewGuid().ToString("N")[..20], HoraColombia.Formatear(ahora), idCliente, tipoEvento,
            idDispositivo, sistemaOperativo, departamentoIp, exitoso ? "exitoso" : "fallido");
        db.Bandeja.Add(new EventoSaliente { Tipo = TipoEventoApp, CargaJson = JsonSerializer.Serialize(ev), CreadoUtc = ahora });
        return ev;
    }

    /// <summary>Encola un aviso genérico para el Escudo (p. ej. desafío biométrico no superado).</summary>
    public void EncolarAviso(string tipo, object carga) =>
        db.Bandeja.Add(new EventoSaliente { Tipo = tipo, CargaJson = JsonSerializer.Serialize(carga), CreadoUtc = reloj.GetUtcNow().UtcDateTime });
}
