using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Scoring;
using Microsoft.EntityFrameworkCore;
using Prometheus;

namespace EscudoYuma.Api.Shared.Expediente;

public static class TiposHecho
{
    public const string Transaccion = "transaccion";
    public const string Decision = "decision";
    public const string EventoApp = "evento_app";
    public const string Ingreso = "ingreso";
    public const string Alerta = "alerta";
    public const string DesafioFallido = "desafio_fallido";
    public const string Resolucion = "resolucion";
    public const string CambioConfiguracion = "cambio_configuracion";
    public const string AlteracionAuditoria = "alteracion_auditoria";
}

public static class FuentesHecho
{
    public const string Billetera = "billetera";
    public const string Escudo = "escudo";
    public const string Analista = "analista";
    public const string Sistema = "sistema";
}

/// <summary>Norma que se puede citar en un expediente. Solo uso interno (analistas y auditoría): reglas y umbrales no se publican.</summary>
public sealed record Norma(string Codigo, string Nombre, string Descripcion);

/// <summary>
/// Catálogo cerrado de normas: reglas vigentes R001-R012 (reglas_vigentes.csv), política calibrada, pisos de seguridad y restricciones
/// del caso. El narrador solo puede citar códigos de este catálogo que aparezcan en el expediente.
/// </summary>
public static class CatalogoNormas
{
    public static readonly IReadOnlyDictionary<string, Norma> Todas = new Norma[]
    {
        new("R001", "Transferencia de monto alto", "Transferencia inmediata igual o superior al tope."),
        new("R002", "Dispositivo nuevo", "Salida de dinero desde un dispositivo registrado recientemente."),
        new("R003", "Operación de madrugada", "Salida de dinero entre las 00:00 y las 04:59."),
        new("R004", "Velocidad de salidas", "Cuatro o más salidas del mismo cliente en 60 minutos."),
        new("R005", "Vaciado de saldo a destino nuevo", "Transferencia a un destino nuevo que consume casi todo el saldo."),
        new("R006", "Cambio de clave reciente", "Transferencia en las 24 horas siguientes a un cambio de clave."),
        new("R007", "Cambio de SIM reciente", "Salida de dinero en las 48 horas siguientes a un cambio de SIM."),
        new("R008", "Cuenta reciente con retiros", "Retiro en efectivo desde una cuenta con menos de 15 días."),
        new("R009", "Ubicación atípica", "Salida de dinero desde un departamento distinto al habitual."),
        new("R010", "Acumulado diario", "Suma de salidas del cliente en el día por encima del tope."),
        new("R011", "Pago QR de monto alto", "Pago con QR igual o superior al tope."),
        new("R012", "Retiro de monto alto", "Retiro en corresponsal o cajero por encima del tope."),
        new("POL-UMBRALES", "Política de decisión calibrada", "Probabilidad del modelo frente a los umbrales calibrados: aprobar, desafiar, retener o rechazar."),
        new("PISO-DISPOSITIVO-SIM-CLAVE", "Piso de seguridad", "Dispositivo nuevo junto con cambio de SIM o de clave recientes en una salida."),
        new("PISO-DISPOSITIVO-SIM-CLAVE-VACIADO", "Piso de seguridad", "Dispositivo nuevo con cambio de SIM o de clave y casi todo el saldo hacia un destino nuevo: pasa a un analista."),
        new("PISO-VACIADO", "Piso de seguridad", "Transferencia que vacía el saldo hacia un destino nuevo (posible ingeniería social)."),
        new("PISO-DESTINO-REPUTACION", "Piso de seguridad", "Destino que recibe de muchos remitentes distintos y tiene reputación baja."),
        new("ESC-INGRESO", "Escalamiento por ingreso riesgoso", "Un ingreso riesgoso sube una aprobación a verificación; nunca baja una decisión."),
        new("NORMA-AUTENTICACION-REFORZADA", "Autenticación reforzada", "Riesgo medio: biometría facial; si no la supera, el caso pasa a un analista."),
        new("NORMA-BLOQUEO-ANALISTA", "Supervisión humana", "Un bloqueo definitivo de cuenta siempre requiere la revisión de un analista."),
        new("NORMA-CADENA-AUDITORIA", "Integridad de la auditoría", "La bitácora es append-only con hash SHA-256 encadenado; cualquier alteración se detecta."),
        new("NORMA-DATOS-PERSONALES", "Protección de datos (Ley 1581 de 2012 y Ley 1266 de 2008)", "Sin datos personales en claro; no se decide por región, edad ni nivel de ingresos."),
    }.ToDictionary(n => n.Codigo);

    public static string Piso(PisoSeguridad p) => p switch
    {
        PisoSeguridad.DispositivoSimClave => "PISO-DISPOSITIVO-SIM-CLAVE",
        PisoSeguridad.DispositivoSimClaveVaciado => "PISO-DISPOSITIVO-SIM-CLAVE-VACIADO",
        PisoSeguridad.Vaciado => "PISO-VACIADO",
        PisoSeguridad.DestinoReputacionBaja => "PISO-DESTINO-REPUTACION",
        _ => "",
    };
}

/// <summary>
/// Singleton: cola acotada de hechos. Registrar NO bloquea (el pipeline de calificación debe responder en menos de 300 ms);
/// <see cref="EscritorExpediente"/> los persiste por lotes con su propio scope.
/// </summary>
public sealed class RegistroExpediente
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly Channel<HechoExpediente> _cola = Channel.CreateBounded<HechoExpediente>(new BoundedChannelOptions(50_000)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    internal ChannelReader<HechoExpediente> Lector => _cola.Reader;
    private long _pendientes;
    public long Pendientes => Interlocked.Read(ref _pendientes);

    public void Registrar(string tipo, string fuente, string? idCliente, string? idTransaccion, object datos, IEnumerable<string>? normas,
        string idCorrelacion, EslabonAuditoria? eslabon, DateTime fechaUtc)
    {
        var h = new HechoExpediente
        {
            Tipo = tipo, Fuente = fuente, IdCliente = idCliente, IdTransaccion = idTransaccion, DatosJson = JsonSerializer.Serialize(datos, Json),
            Normas = (normas ?? []).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList(), IdCorrelacion = idCorrelacion,
            SecuenciaAuditoria = eslabon?.Secuencia, HashAuditoria = eslabon?.Hash,
            // Precisión de segundos, como fecha_hora de los datos del caso; dentro del mismo segundo manda el orden de registro (Id).
            FechaUtc = new DateTime(fechaUtc.Ticks - fechaUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc),
        };
        Interlocked.Increment(ref _pendientes);
        if (!_cola.Writer.TryWrite(h)) Interlocked.Decrement(ref _pendientes);
    }

    internal void Escritos(int n) => Interlocked.Add(ref _pendientes, -n);
}

/// <summary>BackgroundService: persiste los hechos por lotes (hasta 500 por commit).</summary>
public sealed class EscritorExpediente(RegistroExpediente registro, IServiceScopeFactory fabrica, ILogger<EscritorExpediente> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var lote = new List<HechoExpediente>(500);
        while (await registro.Lector.WaitToReadAsync(ct))
        {
            while (lote.Count < 500 && registro.Lector.TryRead(out var h)) lote.Add(h);
            try
            {
                using var scope = fabrica.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EscudoDbContext>();
                db.Hechos.AddRange(lote);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { log.LogError(e, "No se pudieron guardar {N} hechos del expediente.", lote.Count); }
            finally { registro.Escritos(lote.Count); lote.Clear(); }
        }
    }
}

/// <summary>Expediente de una alerta: lo que el analista (y el narrador) necesitan, ordenado en el tiempo y sin datos personales.</summary>
public sealed record ExpedienteAlerta(
    Guid IdAlerta, string IdTransaccion, string IdCliente, long MontoCop, string Decision, double Puntaje, string Estado, IReadOnlyList<string> RazonesEscudo,
    ReputacionExpediente? Reputacion, IReadOnlyList<HechoDto> Hechos, IReadOnlyList<Norma> NormasAplicadas, string Hash);

public sealed record ReputacionExpediente(int PuntajeRiesgo, bool BloqueoDefinitivo, IReadOnlyList<string> Senales);

public sealed record HechoDto(long Id, string Tipo, DateTime FechaUtc, string FechaColombia, string Fuente, string? IdTransaccion,
    JsonElement Datos, IReadOnlyList<string> Normas, long? SecuenciaAuditoria);

/// <summary>Arma el expediente de una alerta: hechos de la operación + hechos del cliente en las 72 h previas (y los posteriores). Scoped.</summary>
public sealed class ConstructorExpediente(EscudoDbContext db)
{
    public static readonly TimeSpan VentanaPrevia = TimeSpan.FromHours(72);
    private const int MaxHechos = 200;

    public async Task<ExpedienteAlerta?> PorAlertaAsync(Guid idAlerta, CancellationToken ct)
    {
        var a = await db.Alertas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == idAlerta, ct);
        if (a is null) return null;
        // Ventana anclada a la hora de la operación (no a la de creación del caso): sirve igual en línea que con datos históricos.
        var ancla = await db.Hechos.AsNoTracking().Where(h => h.IdTransaccion == a.IdTransaccion && h.Tipo == TiposHecho.Transaccion)
            .Select(h => (DateTime?)h.FechaUtc).FirstOrDefaultAsync(ct) ?? a.CreadaUtc;
        var desde = ancla - VentanaPrevia;
        var hechos = await db.Hechos.AsNoTracking()
            .Where(h => h.IdTransaccion == a.IdTransaccion || (h.IdCliente == a.IdCliente && h.FechaUtc >= desde))
            .OrderBy(h => h.FechaUtc).ThenBy(h => h.Id).Take(MaxHechos).ToListAsync(ct);
        var rep = await db.Reputaciones.AsNoTracking().FirstOrDefaultAsync(r => r.IdCuenta == a.IdCliente, ct);
        return Armar(a, hechos, rep);
    }

    public async Task<IReadOnlyList<HechoDto>> PorClienteAsync(string idCliente, DateTime desdeUtc, CancellationToken ct) =>
        (await db.Hechos.AsNoTracking().Where(h => h.IdCliente == idCliente && h.FechaUtc >= desdeUtc)
            .OrderBy(h => h.FechaUtc).ThenBy(h => h.Id).Take(MaxHechos).ToListAsync(ct)).Select(Dto).ToList();

    public static ExpedienteAlerta Armar(Alerta a, IReadOnlyList<HechoExpediente> hechos, ReputacionCuenta? rep)
    {
        var dtos = hechos.Select(Dto).ToList();
        var normas = hechos.SelectMany(h => h.Normas).Distinct().Where(CatalogoNormas.Todas.ContainsKey).OrderBy(c => c, StringComparer.Ordinal)
            .Select(c => CatalogoNormas.Todas[c]).ToList();
        var reputacion = rep is null ? null : new ReputacionExpediente(rep.Puntaje, rep.BloqueoDefinitivo, rep.SenalesAutomaticas.Concat(rep.Senales).ToList());
        var sinHash = new ExpedienteAlerta(a.Id, a.IdTransaccion, a.IdCliente, a.MontoCop, a.Decision, Math.Round(a.Puntaje, 4), a.Estado, a.Razones,
            reputacion, dtos, normas, "");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sinHash)))).ToLowerInvariant();
        return sinHash with { Hash = hash };
    }

    private static HechoDto Dto(HechoExpediente h)
    {
        using var doc = JsonDocument.Parse(h.DatosJson);
        return new HechoDto(h.Id, h.Tipo, h.FechaUtc, Tiempo.AFechaHora(Tiempo.AEpochColombia(h.FechaUtc)), h.Fuente, h.IdTransaccion,
            doc.RootElement.Clone(), h.Normas, h.SecuenciaAuditoria);
    }
}

public static class MetricasExpediente
{
    public static readonly Counter Alteraciones = Metrics.CreateCounter(
        "yuma_auditoria_alteraciones_total", "Verificaciones de la cadena de auditoría que encontraron una alteración.");
}

/// <summary>
/// Verifica periódicamente la cadena de auditoría (CadenaHash.Verificar). Si encuentra un eslabón alterado, borrado o reordenado registra un
/// hecho "alteracion_auditoria" (una vez por secuencia rota) y lo deja en el log como crítico.
/// </summary>
public sealed class VerificadorCadena(IServiceScopeFactory fabrica, RegistroExpediente expediente, IConfiguration cfg, TimeProvider reloj,
    ILogger<VerificadorCadena> log) : BackgroundService
{
    private long? _ultimaRota;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var minutos = cfg.GetValue("Auditoria:MinutosVerificacion", 10);
        if (minutos <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutos), reloj);
        while (await timer.WaitForNextTickAsync(ct))
            try { await VerificarAsync(ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning(e, "No se pudo verificar la cadena de auditoría."); }
    }

    public async Task<ResultadoVerificacion> VerificarAsync(CancellationToken ct)
    {
        using var scope = fabrica.CreateScope();
        var res = await scope.ServiceProvider.GetRequiredService<ServicioAuditoria>().VerificarAsync(ct);
        if (!res.Valida && res.PrimeraSecuenciaRota != _ultimaRota)
        {
            _ultimaRota = res.PrimeraSecuenciaRota;
            MetricasExpediente.Alteraciones.Inc();
            log.LogCritical("Cadena de auditoría alterada a partir de la secuencia {Secuencia} ({Total} registros revisados).", res.PrimeraSecuenciaRota, res.Total);
            expediente.Registrar(TiposHecho.AlteracionAuditoria, FuentesHecho.Sistema, null, null,
                new { primera_secuencia_rota = res.PrimeraSecuenciaRota, registros_revisados = res.Total }, ["NORMA-CADENA-AUDITORIA"],
                "verificador-cadena", null, reloj.GetUtcNow().UtcDateTime);
        }
        return res;
    }
}

/// <summary>Al arrancar deja constancia de la configuración vigente (versión del modelo y huella de los umbrales): "cambio_configuracion".</summary>
public sealed class HechoConfiguracionInicial(RegistroExpediente expediente, ArtefactosOptions artefactos, IConfiguration cfg, TimeProvider reloj) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        var ruta = artefactos.Ruta("umbrales.json");
        var huella = File.Exists(ruta) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ruta)))[..16].ToLowerInvariant() : "sin_archivo";
        expediente.Registrar(TiposHecho.CambioConfiguracion, FuentesHecho.Sistema, null, null, new
        {
            version_modelo = cfg["Modelo:Version"] ?? "desconocida", huella_umbrales = huella,
            umbrales_sobrescritos = cfg.GetSection("Scoring:Umbrales").GetChildren().Select(c => c.Key).ToList(),
        }, ["POL-UMBRALES"], "arranque", null, reloj.GetUtcNow().UtcDateTime);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
