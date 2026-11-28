using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using YumaBilletera.Api.Shared.Operaciones;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Shared.Escudo;

/// <summary>Conexión con el Escudo. Credenciales de la cuenta de servicio SOLO por variables de entorno / secretos.</summary>
public sealed class EscudoOptions
{
    /// <summary>URL base del Escudo (p. ej. http://api:8080). Vacía = no configurado: las operaciones quedan en revisión (falla cerrada).</summary>
    public string? UrlBase { get; set; }
    public string? LlaveServicio { get; set; }
    public string? ClaveServicio { get; set; }
    public string DispositivoServicio { get; set; } = "DISP-billetera";
    public string DepartamentoServicio { get; set; } = "Bogotá D.C.";
    /// <summary>Presupuesto de la consulta de riesgo. El caso exige decisión en menos de 300 ms.</summary>
    public int TimeoutMs { get; set; } = 300;
    /// <summary>Plazo del score de ingreso. El requisito de 300 ms del caso es para calificar operaciones; el ingreso tolera un poco más.</summary>
    public int TimeoutIngresoMs { get; set; } = 1000;
    /// <summary>Fallas seguidas que abren el circuito; mientras está abierto no se llama al Escudo (respuesta inmediata en revisión).</summary>
    public int FallasParaAbrir { get; set; } = 5;
    public int SegundosAbierto { get; set; } = 15;
    /// <summary>Secreto HMAC compartido con el Escudo para validar los avisos de resolución (>= 32 caracteres).</summary>
    public string? SecretoWebhook { get; set; }
    public int SegundosTolerancia { get; set; } = 300;
    /// <summary>false solo en pruebas: el ciclo de integración se ejecuta a mano (determinista).</summary>
    public bool TrabajadorEnSegundoPlano { get; set; } = true;

    public bool Configurado => !string.IsNullOrWhiteSpace(UrlBase);
}

/// <summary>Estado de un caso de analista en el Escudo (GET /v1/transacciones/{id}/decision).</summary>
public sealed record EstadoCasoEscudo(
    [property: JsonPropertyName("id_transaccion")] string IdTransaccion,
    [property: JsonPropertyName("estado_alerta")] string? EstadoAlerta,
    [property: JsonPropertyName("decision_alerta")] string? DecisionAlerta,
    [property: JsonPropertyName("resolucion")] string? Resolucion,
    [property: JsonPropertyName("accion")] string? Accion,
    [property: JsonPropertyName("bloqueo_definitivo_cliente")] bool BloqueoDefinitivoCliente,
    [property: JsonPropertyName("resuelta_utc")] DateTime? ResueltaUtc);

/// <summary>Aviso de resolución que envía el Escudo cuando un analista resuelve el caso de una operación retenida.</summary>
public sealed record AvisoResolucion(
    [property: JsonPropertyName("id_transaccion")] string IdTransaccion,
    [property: JsonPropertyName("id_cliente")] string IdCliente,
    [property: JsonPropertyName("resolucion")] string Resolucion,
    [property: JsonPropertyName("accion")] string Accion,
    [property: JsonPropertyName("bloqueo_definitivo")] bool BloqueoDefinitivo,
    [property: JsonPropertyName("resuelta_utc")] DateTime ResueltaUtc);

/// <summary>Operaciones asíncronas con el Escudo (no están en el camino crítico de una operación).</summary>
public interface IEscudoSincronizacion
{
    Task EnviarEventosAsync(IReadOnlyList<EventoApp> eventos, CancellationToken ct);
    Task ReportarDesafioFallidoAsync(JsonElement carga, CancellationToken ct);
    /// <summary>null si la operación no tiene caso en el Escudo.</summary>
    Task<EstadoCasoEscudo?> ConsultarCasoAsync(string idTransaccion, CancellationToken ct);
}

public sealed class SincronizacionNoConfigurada : IEscudoSincronizacion
{
    public Task EnviarEventosAsync(IReadOnlyList<EventoApp> eventos, CancellationToken ct) => throw new EscudoNoDisponibleException("Escudo no configurado.");
    public Task ReportarDesafioFallidoAsync(JsonElement carga, CancellationToken ct) => throw new EscudoNoDisponibleException("Escudo no configurado.");
    public Task<EstadoCasoEscudo?> ConsultarCasoAsync(string idTransaccion, CancellationToken ct) => throw new EscudoNoDisponibleException("Escudo no configurado.");
}

/// <summary>
/// Adapter HTTP hacia el Escudo (typed HttpClient, Singleton). Autenticación con la cuenta de servicio del Escudo (token de 15 min renovado
/// 1 min antes de vencer). Circuit breaker simple: tras N fallas seguidas no se llama durante unos segundos y se responde "no disponible"
/// de inmediato (la operación queda en revisión sin esperar el timeout). Las calificaciones NO se reintentan: el Escudo actualiza su estado
/// en línea con cada calificación y un reintento la contaría dos veces; la reevaluación posterior la hace el trabajador de integración.
/// </summary>
public sealed class EscudoHttpCliente(HttpClient http, IOptions<EscudoOptions> opciones, TimeProvider reloj, ILogger<EscudoHttpCliente> log)
    : IEscudoCliente, IEscudoSincronizacion
{
    private readonly SemaphoreSlim _candadoToken = new(1, 1);
    private string? _token;
    private DateTime _tokenExpiraUtc;
    private int _fallasSeguidas;
    private DateTime _abiertoHastaUtc;

    private sealed record LoginServicio([property: JsonPropertyName("resultado")] string Resultado, [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("expira_utc")] DateTime? ExpiraUtc);

    public Task<DecisionEscudo> EvaluarTransaccionAsync(TransaccionEscudo tx, CancellationToken ct) =>
        LlamarAsync<DecisionEscudo>(HttpMethod.Post, "/v1/score/transaccion", tx, opciones.Value.TimeoutMs, ct)!;

    public Task<ResultadoIngresoEscudo> EvaluarIngresoAsync(IngresoEscudo ingreso, CancellationToken ct) =>
        LlamarAsync<ResultadoIngresoEscudo>(HttpMethod.Post, "/v1/auth/evaluar-ingreso", ingreso, opciones.Value.TimeoutIngresoMs, ct)!;

    public Task EnviarEventosAsync(IReadOnlyList<EventoApp> eventos, CancellationToken ct) =>
        LlamarAsync<JsonElement>(HttpMethod.Post, "/v1/eventos", new { eventos }, 5_000, ct);

    public Task ReportarDesafioFallidoAsync(JsonElement carga, CancellationToken ct) =>
        LlamarAsync<JsonElement>(HttpMethod.Post, "/v1/alertas/desafio-fallido", carga, 5_000, ct);

    public async Task<EstadoCasoEscudo?> ConsultarCasoAsync(string idTransaccion, CancellationToken ct)
    {
        try { return await LlamarAsync<EstadoCasoEscudo>(HttpMethod.Get, $"/v1/transacciones/{Uri.EscapeDataString(idTransaccion)}/decision", null, 5_000, ct); }
        catch (CasoNoEncontradoException) { return null; }
    }

    private sealed class CasoNoEncontradoException : Exception;

    private async Task<T?> LlamarAsync<T>(HttpMethod metodo, string ruta, object? cuerpo, int timeoutMs, CancellationToken ct)
    {
        var o = opciones.Value;
        if (!o.Configurado) throw new EscudoNoDisponibleException("Escudo no configurado (Escudo:UrlBase).");
        var ahora = reloj.GetUtcNow().UtcDateTime;
        if (ahora < _abiertoHastaUtc) throw new EscudoNoDisponibleException("Circuito abierto: el Escudo falló varias veces seguidas.");

        // El token se obtiene FUERA del presupuesto de la llamada (el login del Escudo usa PBKDF2 y tarda más de 100 ms); normalmente ya está
        // vigente porque el trabajador de integración lo renueva antes de que venza.
        string token;
        try { token = await TokenAsync(ct); }
        catch (Exception e) when ((e is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            Falla();
            throw new EscudoNoDisponibleException("No se pudo obtener el token de servicio del Escudo.", e);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            using var req = new HttpRequestMessage(metodo, new Uri(new Uri(o.UrlBase!), ruta));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (cuerpo is not null) req.Content = JsonContent.Create(cuerpo);
            using var resp = await http.SendAsync(req, cts.Token);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound && metodo == HttpMethod.Get) { Exito(); throw new CasoNoEncontradoException(); }
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) _token = null; // se renovará en la próxima llamada
            if (!resp.IsSuccessStatusCode)
            {
                Falla();
                throw new EscudoNoDisponibleException($"El Escudo respondió {(int)resp.StatusCode} en {ruta}.");
            }
            var res = await resp.Content.ReadFromJsonAsync<T>(cts.Token);
            Exito();
            return res;
        }
        catch (Exception e) when ((e is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            Falla();
            throw new EscudoNoDisponibleException($"El Escudo no respondió en {timeoutMs} ms ({ruta}).", e);
        }
    }

    /// <summary>Renueva el token de servicio si vence en menos de 3 minutos (lo llama el trabajador de integración, fuera de las peticiones).</summary>
    public async Task RenovarTokenAsync(CancellationToken ct)
    {
        if (!opciones.Value.Configurado || (_token is not null && reloj.GetUtcNow().UtcDateTime < _tokenExpiraUtc.AddMinutes(-2))) return;
        try { await TokenAsync(ct, forzar: true); }
        catch (Exception e) when ((e is EscudoNoDisponibleException or HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            log.LogWarning("No se pudo renovar el token de servicio del Escudo: {Mensaje}", e.Message);
        }
    }

    private async Task<string> TokenAsync(CancellationToken ct, bool forzar = false)
    {
        if (!forzar && _token is not null && reloj.GetUtcNow().UtcDateTime < _tokenExpiraUtc) return _token;
        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
        plazo.CancelAfter(TimeSpan.FromSeconds(5));
        ct = plazo.Token;
        await _candadoToken.WaitAsync(ct);
        try
        {
            if (!forzar && _token is not null && reloj.GetUtcNow().UtcDateTime < _tokenExpiraUtc) return _token;
            var o = opciones.Value;
            if (string.IsNullOrEmpty(o.LlaveServicio) || string.IsNullOrEmpty(o.ClaveServicio))
                throw new EscudoNoDisponibleException("Faltan Escudo:LlaveServicio / Escudo:ClaveServicio.");
            using var resp = await http.PostAsJsonAsync(new Uri(new Uri(o.UrlBase!), "/v1/auth/login"), new
            {
                llave = o.LlaveServicio, clave = o.ClaveServicio, id_dispositivo = o.DispositivoServicio, departamento_ip = o.DepartamentoServicio,
            }, ct);
            var login = resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<LoginServicio>(ct) : null;
            if (login?.Token is null || login.ExpiraUtc is null)
            {
                log.LogError("La cuenta de servicio no pudo ingresar al Escudo ({Codigo}, {Resultado}).", (int)resp.StatusCode, login?.Resultado);
                throw new EscudoNoDisponibleException("No se pudo obtener el token de servicio del Escudo.");
            }
            _token = login.Token;
            _tokenExpiraUtc = login.ExpiraUtc.Value.AddMinutes(-1);
            return _token;
        }
        finally { _candadoToken.Release(); }
    }

    private void Exito() => Interlocked.Exchange(ref _fallasSeguidas, 0);

    private void Falla()
    {
        if (Interlocked.Increment(ref _fallasSeguidas) < opciones.Value.FallasParaAbrir) return;
        _abiertoHastaUtc = reloj.GetUtcNow().UtcDateTime.AddSeconds(opciones.Value.SegundosAbierto);
        Interlocked.Exchange(ref _fallasSeguidas, 0);
        log.LogWarning("Circuito hacia el Escudo abierto por {Segundos} s.", opciones.Value.SegundosAbierto);
    }
}

/// <summary>
/// Entrega la bandeja de salida al Escudo en orden (id creciente). Se usa en segundo plano y, antes de calificar una operación o un ingreso,
/// para el cliente en cuestión: así los eventos recientes (p. ej. un ingreso fallido hace segundos) ya están en el estado del Escudo cuando
/// califica (el Escudo no reordena eventos posteriores). Nunca lanza: si falla, el evento queda pendiente. Scoped.
/// </summary>
public sealed class DespachadorBandeja(BilleteraDbContext db, IEscudoSincronizacion escudo, TimeProvider reloj, ILogger<DespachadorBandeja> log)
{
    public const int MaxPorLote = 200;

    public async Task<int> EnviarPendientesAsync(string? idCliente, CancellationToken ct)
    {
        try
        {
            var q = db.Bandeja.Where(b => b.EnviadoUtc == null);
            if (idCliente is not null)
            {
                var marca = $"\"id_cliente\":\"{idCliente}\""; // las cargas se serializan sin espacios
                q = q.Where(b => b.CargaJson.Contains(marca));
            }
            var pendientes = await q.OrderBy(b => b.Id).Take(MaxPorLote).ToListAsync(ct);
            if (pendientes.Count == 0) return 0;

            var enviados = 0;
            // Eventos de la app en un solo lote (en orden); los avisos de desafío fallido uno a uno.
            var eventos = pendientes.Where(b => b.Tipo == PublicadorEventos.TipoEventoApp).ToList();
            if (eventos.Count > 0)
            {
                await escudo.EnviarEventosAsync(eventos.Select(e => JsonSerializer.Deserialize<EventoApp>(e.CargaJson)!).ToList(), ct);
                Marcar(eventos);
                enviados += eventos.Count;
            }
            foreach (var aviso in pendientes.Where(b => b.Tipo == "desafio_fallido"))
            {
                using var doc = JsonDocument.Parse(aviso.CargaJson);
                await escudo.ReportarDesafioFallidoAsync(doc.RootElement.Clone(), ct);
                Marcar([aviso]);
                enviados++;
            }
            await db.SaveChangesAsync(ct);
            return enviados;
        }
        catch (Exception e) when (e is EscudoNoDisponibleException or HttpRequestException or DbUpdateException or OperationCanceledException)
        {
            if (e is OperationCanceledException && !ct.IsCancellationRequested) throw; // cancelación ajena a este despacho
            log.LogDebug(e, "Bandeja hacia el Escudo pendiente: {Mensaje}", e.Message);
            db.ChangeTracker.Clear();
            return 0;
        }
    }

    private void Marcar(IEnumerable<EventoSaliente> lote)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        foreach (var b in lote) { b.EnviadoUtc = ahora; b.Intentos++; }
    }
}

/// <summary>
/// Aplica la resolución de un analista a una operación retenida (aviso firmado del Escudo o conciliación). Idempotente: si la operación ya
/// no está en revisión no hace nada.
/// falso_positivo → se autoriza (el dinero sale) · fraude_confirmado → el dinero no sale (se libera la reserva), salvo
/// mantener_retencion, que deja el monto retenido · bloqueo_definitivo → la cuenta queda bloqueada (decisión del analista en el Escudo). Scoped.
/// </summary>
public sealed class AplicadorResoluciones(BilleteraDbContext db, ProcesadorOperacion procesador, ServicioAuditoria auditoria)
{
    public async Task<bool> AplicarAsync(string idTransaccion, string resolucion, string accion, bool bloqueoDefinitivo, CancellationToken ct)
    {
        var t = await db.Transferencias.FirstOrDefaultAsync(x => x.IdTransaccion == idTransaccion, ct);
        if (t is null) return false;
        var aplicado = false;
        if (t.Estado == EstadosTransferencia.EnRevision)
        {
            if (resolucion == "falso_positivo")
            {
                try { await procesador.CompletarAsync(t, ct); }
                catch (Contabilidad.ReglaContableException) { await procesador.RechazarAsync(t, MensajesCliente.Rechazada, ct); }
                aplicado = true;
            }
            else if (resolucion == "fraude_confirmado" && accion != "mantener_retencion")
            {
                await procesador.RechazarAsync(t, MensajesCliente.Rechazada, ct);
                aplicado = true;
            }
            t.Version = Guid.NewGuid();
        }
        if (bloqueoDefinitivo)
        {
            var cuenta = await db.Cuentas.FirstAsync(c => c.IdCuenta == t.IdClienteOrigen, ct);
            if (cuenta.Estado != EstadosCuenta.Bloqueada) { cuenta.Estado = EstadosCuenta.Bloqueada; cuenta.Version = Guid.NewGuid(); aplicado = true; }
        }
        await db.SaveChangesAsync(ct);
        if (aplicado)
        {
            MetricasBilletera.Operaciones.WithLabels(t.TipoOperacion, t.Estado).Inc();
            await auditoria.RegistrarAsync("resolucion_aplicada", "operaciones/" + t.IdTransaccion,
                new { id_transaccion = t.IdTransaccion, resolucion, accion, bloqueo_definitivo = bloqueoDefinitivo, estado_final = t.Estado }, actor: "escudo", ct: ct);
        }
        return aplicado;
    }

    /// <summary>Valida la firma HMAC del aviso (X-Yuma-Marca-Tiempo / X-Yuma-Firma: sha256=hex de "marca.cuerpo") y la ventana de tiempo.</summary>
    public static bool FirmaValida(string? secreto, string? marca, string? firma, string cuerpo, DateTimeOffset ahora, int toleranciaSegundos)
    {
        if (secreto is not { Length: >= 32 } || firma is null || !long.TryParse(marca, out var ts)) return false;
        if (Math.Abs(ahora.ToUnixTimeSeconds() - ts) > toleranciaSegundos) return false;
        var esperada = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secreto), Encoding.UTF8.GetBytes($"{ts}.{cuerpo}"))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(esperada), Encoding.ASCII.GetBytes(firma));
    }
}

/// <summary>
/// Trabajo en segundo plano de la integración (cada pocos segundos):
/// 1) entrega la bandeja de salida; 2) reevalúa operaciones que quedaron sin decisión porque el Escudo no respondió (y las "evaluando"
/// huérfanas por un reinicio); 3) concilia casos retenidos por si se perdió el aviso de resolución.
/// </summary>
public sealed class TrabajadorIntegracion(IServiceScopeFactory fabrica, IOptions<EscudoOptions> opciones, TimeProvider reloj, ILogger<TrabajadorIntegracion> log)
    : BackgroundService
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(3);
    private DateTime _ultimaConciliacion = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opciones.Value.TrabajadorEnSegundoPlano) return;
        while (!ct.IsCancellationRequested)
        {
            try { await CicloAsync(ct); }
            catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning(e, "Ciclo de integración con el Escudo falló; se reintenta."); }
            await Task.Delay(Intervalo, reloj, ct);
        }
    }

    public async Task CicloAsync(CancellationToken ct)
    {
        if (!opciones.Value.Configurado) return;
        using var scope = fabrica.CreateScope();
        var sp = scope.ServiceProvider;
        if (sp.GetService<EscudoHttpCliente>() is { } http) await http.RenovarTokenAsync(ct); // token listo antes de que una operación lo necesite
        await sp.GetRequiredService<DespachadorBandeja>().EnviarPendientesAsync(null, ct);

        var db = sp.GetRequiredService<BilleteraDbContext>();
        var escudo = sp.GetRequiredService<IEscudoCliente>();
        var procesador = sp.GetRequiredService<ProcesadorOperacion>();
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var pendientes = await db.Transferencias
            .Where(t => (t.Estado == EstadosTransferencia.EnRevision && t.Decision == null && t.CreadaUtc < ahora.AddSeconds(-5))
                        || (t.Estado == EstadosTransferencia.Evaluando && t.CreadaUtc < ahora.AddMinutes(-1)))
            .OrderBy(t => t.CreadaUtc).Take(50).ToListAsync(ct);
        foreach (var t in pendientes)
        {
            DecisionEscudo d;
            try { d = await escudo.EvaluarTransaccionAsync(ProcesadorOperacion.ATransaccionEscudo(t), ct); }
            catch (EscudoNoDisponibleException) { break; } // sigue caído: se intenta en el próximo ciclo
            try
            {
                await procesador.AplicarDecisionAsync(t, d, ct);
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // La cuenta cambió por una petición del cliente en ese instante: se reintenta en el próximo ciclo.
                db.ChangeTracker.Clear();
                continue;
            }
            await sp.GetRequiredService<ServicioAuditoria>().RegistrarAsync("operacion_reevaluada", "operaciones/" + t.IdTransaccion,
                new { id_transaccion = t.IdTransaccion, decision = d.Decision, estado_final = t.Estado }, actor: "trabajador_integracion", ct: ct);
        }

        await VencerVerificacionesAsync(db, procesador, sp.GetRequiredService<ServicioAuditoria>(), ahora, ct);

        if (ahora - _ultimaConciliacion < TimeSpan.FromMinutes(1)) return;
        _ultimaConciliacion = ahora;
        var sync = sp.GetRequiredService<IEscudoSincronizacion>();
        var aplicador = sp.GetRequiredService<AplicadorResoluciones>();
        var retenidas = await db.Transferencias.AsNoTracking()
            .Where(t => t.Estado == EstadosTransferencia.EnRevision && t.Decision != null).OrderBy(t => t.CreadaUtc).Select(t => t.IdTransaccion).Take(100).ToListAsync(ct);
        foreach (var id in retenidas)
        {
            EstadoCasoEscudo? caso;
            try { caso = await sync.ConsultarCasoAsync(id, ct); }
            catch (EscudoNoDisponibleException) { break; }
            if (caso is { EstadoAlerta: "resuelta", Resolucion: { } r, Accion: { } a })
                await aplicador.AplicarAsync(id, r, a, caso.BloqueoDefinitivoCliente, ct);
        }
    }

    /// <summary>
    /// Verificación de transferencia vencida sin respuesta (el cliente cerró la app o abandonó la verificación facial): se trata como no
    /// superada. La operación pasa a revisión con el monto reservado y el caso llega al analista por la bandeja (desafio_fallido). Sin este
    /// barrido el dinero quedaría reservado indefinidamente y nadie revisaría una operación que el Escudo ya marcó como riesgosa.
    /// </summary>
    public static async Task VencerVerificacionesAsync(BilleteraDbContext db, ProcesadorOperacion procesador, ServicioAuditoria auditoria,
        DateTime ahora, CancellationToken ct)
    {
        var vencidas = await db.Transferencias
            .Where(t => t.Estado == EstadosTransferencia.RequiereVerificacion && t.CreadaUtc < ahora.AddSeconds(-10)
                        && !db.Verificaciones.Any(v => v.Proposito == PropositosVerificacion.Transferencia && v.Referencia == t.IdTransaccion
                                                       && !v.Resuelta && v.ExpiraUtc > ahora))
            .OrderBy(t => t.CreadaUtc).Take(50).ToListAsync(ct);
        foreach (var t in vencidas)
        {
            var abiertas = await db.Verificaciones
                .Where(v => v.Proposito == PropositosVerificacion.Transferencia && v.Referencia == t.IdTransaccion && !v.Resuelta).ToListAsync(ct);
            abiertas.ForEach(v => v.Resuelta = true);
            procesador.PasarARevisionPorVerificacion(t, "El cliente no completó la verificación facial a tiempo.");
            t.Version = Guid.NewGuid();
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            {
                // El cliente respondió la verificación en ese mismo instante: gana su respuesta.
                db.ChangeTracker.Clear();
                continue;
            }
            MetricasBilletera.Operaciones.WithLabels(t.TipoOperacion, t.Estado).Inc();
            await auditoria.RegistrarAsync("operacion_verificacion_vencida", "operaciones/" + t.IdTransaccion,
                new { id_transaccion = t.IdTransaccion, estado_final = t.Estado }, actor: "trabajador_integracion", ct: ct);
        }
    }
}

public static class IntegracionEscudoExtensions
{
    /// <summary>Adapter HTTP + bandeja + trabajador. Sin Escudo:UrlBase se usan las implementaciones "no configurado" (falla cerrada).</summary>
    public static IServiceCollection AddIntegracionEscudo(this IServiceCollection s)
    {
        s.AddOptions<EscudoOptions>().BindConfiguration("Escudo");
        s.AddHttpClient(nameof(EscudoHttpCliente));
        // Singleton: el token y el estado del circuito se comparten por proceso.
        s.AddSingleton(sp => new EscudoHttpCliente(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(EscudoHttpCliente)),
            sp.GetRequiredService<IOptions<EscudoOptions>>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<EscudoHttpCliente>>()));
        s.AddSingleton<IEscudoCliente>(sp => sp.GetRequiredService<IOptions<EscudoOptions>>().Value.Configurado
            ? sp.GetRequiredService<EscudoHttpCliente>() : new EscudoNoConfigurado());
        s.AddSingleton<IEscudoSincronizacion>(sp => sp.GetRequiredService<IOptions<EscudoOptions>>().Value.Configurado
            ? sp.GetRequiredService<EscudoHttpCliente>() : new SincronizacionNoConfigurada());
        s.AddScoped<DespachadorBandeja>();
        s.AddScoped<AplicadorResoluciones>();
        s.AddHostedService<TrabajadorIntegracion>();
        return s;
    }
}
