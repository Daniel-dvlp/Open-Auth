using System.Diagnostics;
using Prometheus;
using YumaBilletera.Api.Shared.AutenticacionReforzada;
using YumaBilletera.Api.Shared.Contabilidad;
using YumaBilletera.Api.Shared.Escudo;
using YumaBilletera.Api.Shared.Persistencia;
using YumaBilletera.Api.Shared.Seguridad;

namespace YumaBilletera.Api.Shared.Operaciones;

/// <summary>Métricas Prometheus de la billetera.</summary>
public static class MetricasBilletera
{
    public static readonly Counter Operaciones = Metrics.CreateCounter(
        "billetera_operaciones_total", "Operaciones que mueven dinero por tipo y estado resultante.", "tipo_operacion", "estado");
    public static readonly Histogram LatenciaEscudo = Metrics.CreateHistogram(
        "billetera_escudo_latencia_ms", "Latencia de la consulta al Escudo (ms).",
        new HistogramConfiguration { Buckets = [5, 10, 25, 50, 100, 200, 300, 500, 1000] });
    public static readonly Counter FallasEscudo = Metrics.CreateCounter(
        "billetera_escudo_fallas_total", "Consultas al Escudo sin respuesta (la operación queda en revisión: falla cerrada).", "operacion");
    public static readonly Counter Ingresos = Metrics.CreateCounter(
        "billetera_ingresos_total", "Resultados del ingreso a la app.", "resultado");
}

/// <summary>Mensajes al cliente. No revelan reglas, umbrales ni puntajes (el caso prohíbe publicarlos: servirían a los delincuentes).</summary>
public static class MensajesCliente
{
    public const string Exitosa = "Operación exitosa.";
    public const string Verificar = "Por tu seguridad, confirma que eres tú con la verificación facial para completar la operación.";
    public const string Advertencia =
        "Atención: Yuma Pay nunca te pedirá trasladar tu dinero a una \"cuenta segura\" ni hacer envíos guiados por llamada. " +
        "Si alguien te está indicando hacer este envío, cancélalo. Si tú lo decidiste, confirma con la verificación facial.";
    public const string EnRevision = "Tu operación quedó en revisión por seguridad. El dinero sigue en tu cuenta y te avisaremos el resultado.";
    public const string Rechazada = "No pudimos procesar esta operación por seguridad. Tu dinero sigue en tu cuenta.";
    public const string VerificacionFallida = "No pudimos confirmar tu identidad. Un analista revisará la operación; el dinero sigue en tu cuenta.";
}

/// <summary>Respuesta común de toda operación que mueve dinero (transferencia, pago QR, pago de servicios, recarga).</summary>
public sealed record OperacionResponse(
    string IdTransaccion, string TipoOperacion, string Estado, long MontoCop, string TipoDestino, string IdDestino, string? Descripcion,
    string Mensaje, VerificacionPendiente? Verificacion, DateTime CreadaUtc, DateTime? ResueltaUtc)
{
    public static OperacionResponse De(Transferencia t, Persistencia.Verificacion? v = null) => new(
        t.IdTransaccion, t.TipoOperacion, t.Estado, t.MontoCop, t.TipoDestino, t.IdDestino, t.Descripcion,
        t.MensajeCliente ?? MensajesCliente.EnRevision,
        v is null ? null : new VerificacionPendiente(v.Id, v.Metodo, v.ExpiraUtc), t.CreadaUtc, t.ResueltaUtc);
}

public sealed record VerificacionPendiente(Guid IdVerificacion, string Metodo, DateTime ExpiraUtc);

/// <summary>
/// Topes de operación. <see cref="LimiteDiarioCop"/> = 0 desactiva el tope diario (el caso no fija uno: se define por configuración,
/// Billetera:Limites:LimiteDiarioCop). El máximo por operación es el mismo que acepta el Escudo.
/// </summary>
public sealed class LimitesOperacion
{
    public const long MaxMontoCop = 1_000_000_000_000;
    public long LimiteDiarioCop { get; set; }
}

/// <summary>Lo que cada caso de uso (slice) entrega al pipeline.</summary>
public sealed record SolicitudOperacion(
    string IdCliente, string IdSesion, string ClaveIdempotencia, string TipoOperacion, string TipoDestino, string IdDestino,
    string IdCuentaCredito, long MontoCop, string? Descripcion);

/// <summary>Error de negocio esperado (se traduce a ProblemDetails con el código HTTP indicado).</summary>
public sealed class OperacionException(int status, string codigo, string mensaje) : Exception(mensaje)
{
    public int Status { get; } = status;
    public string Codigo { get; } = codigo;
    public IResult AProblema() => Results.Problem(title: Message, statusCode: Status, extensions: new Dictionary<string, object?> { ["codigo"] = Codigo });
}

/// <summary>
/// Construye las señales de riesgo del esquema Transaccion con datos de la billetera (sin información del futuro: solo lo ocurrido
/// antes de "ahora"). Definiciones del diccionario de datos del caso:
/// destino_nuevo = primera operación hacia ese destino en 180 días · dispositivo_nuevo = registrado en las últimas 24 h ·
/// cambio_clave_24h / cambio_sim_48h · ip_departamento_distinto = departamento de la IP distinto al habitual del cliente ·
/// antiguedad_cuenta_dias = días entre la vinculación y la operación.
/// </summary>
public sealed class ConstructorSenales(BilleteraDbContext db)
{
    public async Task<(int DestinoNuevo, int DispositivoNuevo, int CambioClave24h, int CambioSim48h, int IpDistinta, int Antiguedad)> CalcularAsync(
        Cliente cliente, SesionCliente sesion, string idDestino, DateTime ahoraUtc, CancellationToken ct)
    {
        var historial = await db.HistorialDestinos.FirstOrDefaultAsync(h => h.IdCliente == cliente.IdCliente && h.IdDestino == idDestino, ct);
        var destinoNuevo = historial is null || historial.UltimaOperacionUtc < ahoraUtc.AddDays(-180) ? 1 : 0;
        var disp = await db.Dispositivos.FirstOrDefaultAsync(d => d.IdCliente == cliente.IdCliente && d.IdDispositivo == sesion.IdDispositivo, ct);
        var dispositivoNuevo = disp is null || disp.RegistradoUtc > ahoraUtc.AddHours(-24) ? 1 : 0;
        var cambioClave = await CambioRecienteAsync(cliente.IdCliente, TiposEventoSeguridad.CambioClave, ahoraUtc.AddHours(-24), ahoraUtc, ct);
        var cambioSim = await CambioRecienteAsync(cliente.IdCliente, TiposEventoSeguridad.CambioSim, ahoraUtc.AddHours(-48), ahoraUtc, ct);
        var ipDistinta = string.Equals(sesion.DepartamentoIp, cliente.Departamento, StringComparison.Ordinal) ? 0 : 1;
        var hoy = DateOnly.FromDateTime(HoraColombia.DesdeUtc(ahoraUtc));
        var antiguedad = Math.Max(0, hoy.DayNumber - cliente.FechaVinculacion.DayNumber);
        return (destinoNuevo, dispositivoNuevo, cambioClave, cambioSim, ipDistinta, antiguedad);
    }

    public Task<int> CambioSim48hAsync(string idCliente, DateTime ahoraUtc, CancellationToken ct) =>
        CambioRecienteAsync(idCliente, TiposEventoSeguridad.CambioSim, ahoraUtc.AddHours(-48), ahoraUtc, ct);

    private async Task<int> CambioRecienteAsync(string idCliente, string tipo, DateTime desde, DateTime hasta, CancellationToken ct) =>
        await db.EventosSeguridad.AnyAsync(e => e.IdCliente == idCliente && e.Tipo == tipo && e.FechaUtc >= desde && e.FechaUtc <= hasta, ct) ? 1 : 0;
}

/// <summary>
/// Pipeline de toda operación que mueve dinero (mismo patrón Pipeline del Escudo):
/// idempotencia → validación de sesión y destino → señales → reserva del monto → Escudo (antes de autorizar) → decisión → auditoría.
/// Regla de oro: ningún débito se asienta sin decisión del Escudo; si el Escudo no responde, la operación queda en revisión (falla cerrada).
/// Scoped.
/// </summary>
public sealed class ProcesadorOperacion(
    BilleteraDbContext db, LibroMayor libro, ConstructorSenales senales, IEscudoCliente escudo, ServicioVerificacion verificaciones,
    PublicadorEventos publicador, DespachadorBandeja bandeja, ServicioAuditoria auditoria, TimeProvider reloj, Microsoft.Extensions.Options.IOptions<LimitesOperacion> limites,
    ILogger<ProcesadorOperacion> log)
{
    private const int MaxReintentos = 5;

    public async Task<(OperacionResponse Respuesta, bool Nueva)> EjecutarAsync(SolicitudOperacion s, CancellationToken ct)
    {
        // 1) Idempotencia: un reintento de la app con la misma clave devuelve la misma operación (nunca dos envíos).
        var previa = await db.Transferencias.AsNoTracking()
            .FirstOrDefaultAsync(t => t.IdClienteOrigen == s.IdCliente && t.ClaveIdempotencia == s.ClaveIdempotencia, ct);
        if (previa is not null)
        {
            if (previa.TipoOperacion != s.TipoOperacion || previa.IdDestino != s.IdDestino || previa.MontoCop != s.MontoCop)
                throw new OperacionException(409, "idempotencia_conflicto", "La clave de idempotencia ya se usó con otra operación.");
            return (OperacionResponse.De(previa, await VerificacionPendienteAsync(previa, ct)), false);
        }

        var ahora = reloj.GetUtcNow().UtcDateTime;
        var sesion = await db.Sesiones.AsNoTracking().FirstOrDefaultAsync(x => x.IdSesion == s.IdSesion && x.IdCliente == s.IdCliente, ct);
        if (sesion is null || sesion.Estado != EstadosSesion.Activa || sesion.ExpiraUtc < ahora)
            throw new OperacionException(401, "sesion_invalida", "La sesión no está activa. Ingresa de nuevo.");
        var cliente = await db.Clientes.AsNoTracking().FirstAsync(c => c.IdCliente == s.IdCliente, ct);
        if (s.IdCuentaCredito == s.IdCliente)
            throw new OperacionException(422, "misma_cuenta", "No puedes enviarte dinero a tu propia cuenta.");
        var credito = await db.Cuentas.AsNoTracking().FirstOrDefaultAsync(c => c.IdCuenta == s.IdCuentaCredito, ct)
            ?? throw new OperacionException(404, "destino_inexistente", "El destino no existe.");
        if (credito.Estado == EstadosCuenta.Bloqueada)
            throw new OperacionException(422, "destino_bloqueado", "La cuenta de destino no puede recibir dinero.");

        // Tope diario (día calendario de Colombia), contando lo enviado y lo reservado; no cuenta lo rechazado.
        if (limites.Value.LimiteDiarioCop > 0)
        {
            var inicioDia = HoraColombia.AUtc(HoraColombia.DesdeUtc(ahora).Date.ToString(HoraColombia.Formato, System.Globalization.CultureInfo.InvariantCulture));
            var usado = await db.Transferencias.AsNoTracking()
                .Where(t => t.IdClienteOrigen == s.IdCliente && t.CreadaUtc >= inicioDia && t.Estado != EstadosTransferencia.Rechazada)
                .SumAsync(t => t.MontoCop, ct);
            if (usado + s.MontoCop > limites.Value.LimiteDiarioCop)
                throw new OperacionException(422, "limite_diario", "La operación supera tu tope diario de envíos.");
        }

        // 2) Señales (solo con información anterior a la operación) y reserva del monto con control de concurrencia.
        var sn = await senales.CalcularAsync(cliente, sesion, s.IdDestino, ahora, ct);
        var tx = new Transferencia
        {
            IdTransaccion = NuevoId(ahora), IdClienteOrigen = s.IdCliente, TipoOperacion = s.TipoOperacion, Canal = "app",
            IdDestino = s.IdDestino, TipoDestino = s.TipoDestino, IdCuentaCredito = s.IdCuentaCredito, MontoCop = s.MontoCop,
            Descripcion = s.Descripcion, ClaveIdempotencia = s.ClaveIdempotencia, IdSesion = sesion.IdSesion,
            IdDispositivo = sesion.IdDispositivo, DepartamentoIp = sesion.DepartamentoIp,
            DestinoNuevo = sn.DestinoNuevo, DispositivoNuevo = sn.DispositivoNuevo, CambioClave24h = sn.CambioClave24h, CambioSim48h = sn.CambioSim48h,
            IpDepartamentoDistinto = sn.IpDistinta, AntiguedadCuentaDias = sn.Antiguedad, CreadaUtc = ahora,
        };
        await ConReintentoAsync(async () =>
        {
            var origen = await libro.ObtenerAsync(s.IdCliente, ct);
            tx.SaldoAntesCop = origen.DisponibleCop;
            try { LibroMayor.Reservar(origen, s.MontoCop); }
            catch (ReglaContableException e) { throw new OperacionException(422, e.Codigo, e.Message); }
            db.Transferencias.Add(tx);
            await db.SaveChangesAsync(ct);
        }, ct);

        // 3) Escudo ANTES de autorizar (fuera de la transacción de BD: no se retienen bloqueos mientras se espera la red).
        DecisionEscudo? decision = null;
        var sw = Stopwatch.StartNew();
        await EntregarEventosPreviosAsync(bandeja, s.IdCliente, ct);
        try
        {
            decision = await escudo.EvaluarTransaccionAsync(ATransaccionEscudo(tx), ct);
        }
        catch (Exception e) when ((e is EscudoNoDisponibleException or HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            MetricasBilletera.FallasEscudo.WithLabels("transaccion").Inc();
            log.LogWarning(e, "Escudo sin respuesta para {IdTransaccion}: queda en revisión (falla cerrada).", tx.IdTransaccion);
        }
        finally { MetricasBilletera.LatenciaEscudo.Observe(sw.Elapsed.TotalMilliseconds); }

        // 4) Aplicar la decisión.
        Persistencia.Verificacion? verificacion = null;
        await ConReintentoAsync(async () =>
        {
            var t = await db.Transferencias.FirstAsync(x => x.IdTransaccion == tx.IdTransaccion, ct);
            verificacion = await AplicarDecisionAsync(t, decision, ct);
            await db.SaveChangesAsync(ct);
            tx = t;
        }, ct);

        MetricasBilletera.Operaciones.WithLabels(tx.TipoOperacion, tx.Estado).Inc();
        await auditoria.RegistrarAsync("operacion_" + tx.Estado, "operaciones/" + tx.IdTransaccion, new
        {
            id_transaccion = tx.IdTransaccion, tipo_operacion = tx.TipoOperacion, monto_cop = tx.MontoCop, tipo_destino = tx.TipoDestino,
            decision = tx.Decision ?? "sin_respuesta_escudo", decision_interna = tx.DecisionInterna, puntaje = tx.Puntaje, razones = tx.Razones,
            latencia_escudo_ms = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
        }, actor: s.IdCliente, ct: ct);
        return (OperacionResponse.De(tx, verificacion), true);
    }

    /// <summary>
    /// Aplica la decisión del Escudo a una operación con el monto reservado (o null si el Escudo no respondió: queda en revisión).
    /// Lo usan el pipeline y la reevaluación de operaciones que quedaron sin decisión. El llamador guarda.
    /// </summary>
    public async Task<Persistencia.Verificacion?> AplicarDecisionAsync(Transferencia t, DecisionEscudo? decision, CancellationToken ct)
    {
        Persistencia.Verificacion? verificacion = null;
        if (decision is not null)
        {
            t.Decision = decision.Decision;
            t.DecisionInterna = decision.DecisionInterna;
            t.Puntaje = decision.Puntaje;
            t.Razones = decision.Razones.ToList();
            t.VersionModelo = decision.VersionModelo;
        }
        switch (decision?.Decision)
        {
            case DecisionesEscudo.Aprobar:
                try { await CompletarAsync(t, ct); }
                catch (ReglaContableException) { await RechazarAsync(t, MensajesCliente.Rechazada, ct); } // p. ej. destino bloqueado mientras se evaluaba
                break;
            case DecisionesEscudo.Desafiar:
                t.Estado = EstadosTransferencia.RequiereVerificacion;
                t.MensajeCliente = decision.DecisionInterna == DecisionesEscudo.Advertir ? MensajesCliente.Advertencia : MensajesCliente.Verificar;
                verificacion = await verificaciones.CrearAsync(PropositosVerificacion.Transferencia, MetodosVerificacion.Biometria, t.IdTransaccion, t.IdClienteOrigen, ct);
                break;
            case DecisionesEscudo.Rechazar:
                await RechazarAsync(t, MensajesCliente.Rechazada, ct);
                break;
            default: // retener, decisión desconocida o Escudo sin respuesta: el dinero no sale
                t.Estado = EstadosTransferencia.EnRevision;
                t.MensajeCliente = MensajesCliente.EnRevision;
                break;
        }
        t.Version = Guid.NewGuid();
        return verificacion;
    }

    /// <summary>Asienta el débito/crédito consumiendo la reserva y marca la operación completada. El llamador guarda.</summary>
    public async Task CompletarAsync(Transferencia t, CancellationToken ct)
    {
        var origen = await libro.ObtenerAsync(t.IdClienteOrigen, ct);
        var destino = await libro.ObtenerAsync(t.IdCuentaCredito, ct);
        libro.Asentar(origen, destino, t.MontoCop, t.TipoOperacion, t.IdTransaccion, consumirReserva: true);
        t.Estado = EstadosTransferencia.Completada;
        t.MensajeCliente = MensajesCliente.Exitosa;
        t.ResueltaUtc = reloj.GetUtcNow().UtcDateTime;
        var h = await db.HistorialDestinos.FirstOrDefaultAsync(x => x.IdCliente == t.IdClienteOrigen && x.IdDestino == t.IdDestino, ct);
        if (h is null) db.HistorialDestinos.Add(new HistorialDestino { IdCliente = t.IdClienteOrigen, IdDestino = t.IdDestino, UltimaOperacionUtc = t.ResueltaUtc.Value });
        else h.UltimaOperacionUtc = t.ResueltaUtc.Value;
    }

    /// <summary>Libera la reserva y marca la operación rechazada. El llamador guarda.</summary>
    public async Task RechazarAsync(Transferencia t, string mensaje, CancellationToken ct)
    {
        LibroMayor.LiberarReserva(await libro.ObtenerAsync(t.IdClienteOrigen, ct), t.MontoCop);
        t.Estado = EstadosTransferencia.Rechazada;
        t.MensajeCliente = mensaje;
        t.ResueltaUtc = reloj.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// Biometría no superada: el caso pasa a un analista (diagrama "Flujo de autorización": ¿la supera? no → crear caso con explicación).
    /// El monto sigue reservado y se avisa al Escudo por la bandeja de salida. El llamador guarda.
    /// </summary>
    public void PasarARevisionPorVerificacion(Transferencia t, string detalle)
    {
        t.Estado = EstadosTransferencia.EnRevision;
        t.MensajeCliente = MensajesCliente.VerificacionFallida;
        publicador.EncolarAviso("desafio_fallido", new
        {
            id_transaccion = t.IdTransaccion, id_cliente = t.IdClienteOrigen, monto_cop = t.MontoCop, metodo = MetodosVerificacion.Biometria,
            detalle, decision_escudo = t.Decision, puntaje = t.Puntaje, razones = t.Razones,
        });
    }

    /// <summary>
    /// Entrega al Escudo los eventos pendientes del cliente (p. ej. un ingreso fallido de hace segundos) antes de calificarlo, con un presupuesto
    /// corto: si no alcanza, el evento sigue en la bandeja y la operación continúa.
    /// </summary>
    public static async Task EntregarEventosPreviosAsync(DespachadorBandeja bandeja, string idCliente, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));
        try { await bandeja.EnviarPendientesAsync(idCliente, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // se acabó su presupuesto: los eventos siguen en la bandeja
    }

    public static TransaccionEscudo ATransaccionEscudo(Transferencia t) => new(
        t.IdTransaccion, HoraColombia.Formatear(t.CreadaUtc), t.IdClienteOrigen, t.TipoOperacion, t.Canal, t.MontoCop, t.SaldoAntesCop,
        t.IdDestino, t.TipoDestino, t.DestinoNuevo, t.IdDispositivo, t.DispositivoNuevo, t.CambioClave24h, t.CambioSim48h,
        t.DepartamentoIp, t.IpDepartamentoDistinto, t.AntiguedadCuentaDias);

    private Task<Persistencia.Verificacion?> VerificacionPendienteAsync(Transferencia t, CancellationToken ct) =>
        t.Estado != EstadosTransferencia.RequiereVerificacion
            ? Task.FromResult<Persistencia.Verificacion?>(null)
            : db.Verificaciones.AsNoTracking().Where(v => v.Proposito == PropositosVerificacion.Transferencia && v.Referencia == t.IdTransaccion && !v.Resuelta)
                .OrderByDescending(v => v.ExpiraUtc).FirstOrDefaultAsync(ct);

    /// <summary>Reintenta ante conflicto de concurrencia optimista (otra operación cambió la cuenta entre la lectura y el guardado).</summary>
    private async Task ConReintentoAsync(Func<Task> trabajo, CancellationToken ct)
    {
        for (var intento = 1; ; intento++)
        {
            try { await trabajo(); return; }
            catch (DbUpdateConcurrencyException) when (intento < MaxReintentos && !ct.IsCancellationRequested)
            {
                db.ChangeTracker.Clear();
                await Task.Delay(Random.Shared.Next(5, 25) * intento, ct); // pequeña espera aleatoria para no chocar otra vez
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                throw new OperacionException(409, "reintentar", "Tu cuenta está procesando otra operación. Intenta de nuevo en unos segundos.");
            }
        }
    }

    /// <summary>Id de la operación (≤ 32 caracteres, admitido por el Escudo): "TY" + fecha Colombia + aleatorio.</summary>
    private static string NuevoId(DateTime ahoraUtc) =>
        "TY" + HoraColombia.DesdeUtc(ahoraUtc).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)
        + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(5));
}

/// <summary>Ayudas de endpoint comunes a los slices que mueven dinero.</summary>
public static class EndpointsOperacion
{
    public const string CabeceraIdempotencia = "Idempotency-Key";

    /// <summary>La app debe enviar Idempotency-Key (1-64 caracteres [A-Za-z0-9_-]) para que un reintento no duplique el envío.</summary>
    public static string? LeerClave(HttpContext http)
    {
        var k = http.Request.Headers[CabeceraIdempotencia].FirstOrDefault();
        return k is { Length: >= 1 and <= 64 } && k.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? k : null;
    }

    public static IResult FaltaClave() => Results.ValidationProblem(new Dictionary<string, string[]>
        { [CabeceraIdempotencia] = ["La cabecera Idempotency-Key es obligatoria (1-64 caracteres: letras, números, - o _)."] });

    /// <summary>201 si la operación es nueva, 200 si es un reintento con la misma clave; errores de negocio como ProblemDetails.</summary>
    public static async Task<IResult> ResponderAsync(Func<Task<(OperacionResponse Respuesta, bool Nueva)>> ejecutar)
    {
        try
        {
            var (resp, nueva) = await ejecutar();
            return nueva ? Results.Created($"/v1/transferencias/{resp.IdTransaccion}", resp) : Results.Ok(resp);
        }
        catch (OperacionException e) { return e.AProblema(); }
    }
}
