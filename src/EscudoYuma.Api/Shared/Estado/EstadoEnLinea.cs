using System.Globalization;
using System.Text.Json.Serialization;
using EscudoYuma.Api.Shared.Scoring;

namespace EscudoYuma.Api.Shared.Estado;

/// <summary>Tiempo en segundos enteros sobre la hora local de Colombia (naive), igual que training/features.py.</summary>
public static class Tiempo
{
    public const long Min = 60, Hora = 3600, Dia = 86400;
    public const double TopeMinSinEvento = 10080; // 7 días en minutos (valor cuando no hay evento)
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public static long AEpoch(DateTime d) => (long)(d - Epoch).TotalSeconds;
    public static long AEpoch(string fechaHora) => AEpoch(DateTime.ParseExact(fechaHora, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    public static long FechaAEpoch(string fecha) => AEpoch(DateTime.ParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture));
    /// <summary>Hora local de Colombia (UTC-5, sin horario de verano) a partir de un instante UTC.</summary>
    public static long AEpochColombia(DateTime utc) => AEpoch(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified).AddHours(-5));
    public static string AFechaHora(long epoch) => Epoch.AddSeconds(epoch).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

public static class TiposEvento
{
    public const string InicioSesion = "inicio_sesion";
    public const string RegistroDispositivo = "registro_dispositivo";
    public const string CambioClave = "cambio_clave";
    public const string CambioSim = "cambio_sim_detectado";
    public static readonly string[] Todos = [InicioSesion, RegistroDispositivo, CambioClave, CambioSim];
}

/// <summary>Evento de la app (esquema de eventos_app.csv / `Evento` del openapi del sandbox).</summary>
public sealed record EventoApp(
    string IdEvento, string FechaHora, string IdCliente, string TipoEvento,
    string? IdDispositivo = null, string? SistemaOperativo = null, string? DepartamentoIp = null, string? Resultado = "exitoso")
{
    private long? _ts;
    [JsonIgnore] public long Ts => _ts ??= Tiempo.AEpoch(FechaHora);
    [JsonIgnore] public bool Fallido => Resultado == "fallido";
}

public sealed record SesionIngreso(string IdSesion, string IdCliente, double ScoreIngreso, DateTime ExpiraUtc);

/// <summary>Reputación de una cuenta (0-100, 100 = confiable) y las señales que la degradan.</summary>
public sealed record ReputacionCalculada(string IdCuenta, double Reputacion, IReadOnlyList<string> Senales);

/// <summary>Instantánea del estado de un cliente que necesita el score de ingreso (training/score_login.py).</summary>
public sealed record ContextoLogin(
    bool TieneDispositivos, bool DispositivoConocido, int Fallidos1h, int Fallidos24h,
    bool CambioSim48h, bool CambioClave24h, bool Registro24h,
    string? DepartamentoIp, string? IpHabitual, int ObservacionesIp, int Hora);

/// <summary>Resultado atómico de procesar una transacción: atributos (estado previo) y reputaciones tras actualizar.</summary>
public sealed record ResultadoEstadoTx(AtributosTransaccion Atributos, ReputacionCalculada Origen, ReputacionCalculada? Destino);

/// <summary>
/// Lectura del estado usada por el constructor de atributos. Los métodos PURGAN ventanas igual que features.py
/// y NO tienen candado: solo deben invocarse desde <see cref="IEstadoEnLinea.ProcesarTx"/>, que ya lo posee.
/// </summary>
public interface IVistaEstado
{
    (int N, double Media, long P95, long? UltimaTx) Historial(string cliente);
    int ConteoTx60(string cliente, long ts);
    (int S10, int S60) Salidas(string cliente, long ts);
    long AcumuladoDiaPrevio(string cliente, long ts);
    int DestinosNuevos24h(string cliente, long ts);
    int Fallidos(string cliente, long ts, long ventana);
    long? UltimoRegistro(string cliente);
    long? UltimoCambioClave(string cliente);
    long? UltimoCambioSim(string cliente);
    bool DispositivoVisto(string cliente, string dispositivo);
    int FanIn(string destino, long ts);
    int NTxDestino7d(string destino);
    (int Recibidas, int ConDestinoNuevo) HistoricoDestino(string destino);
    (int N24, long M24, int N7, long M7) Entradas(string cuenta, long ts);
    long SalidasMonto24h(string cuenta, long ts);
    double Reputacion(string cuenta, string? tipoDestino, long ts);
}

/// <summary>
/// Estado en línea de baja latencia. Singleton: es el estado compartido entre requests (ventanas por cliente y destino,
/// eventos de la app, score de ingreso por sesión). Hoy en memoria; con varias réplicas se movería a Redis.
/// </summary>
public interface IEstadoEnLinea
{
    /// <summary>Fecha de vinculación (epoch) por cuenta Yuma: dato estático (clientes.csv / BD), usado en la reputación.</summary>
    void CargarVinculaciones(IEnumerable<KeyValuePair<string, long>> vinculaciones);
    /// <summary>Borra todo el estado dinámico (conserva las vinculaciones). Lo usa el replay del sandbox.</summary>
    void Reiniciar();

    void ProcesarEvento(EventoApp evento);
    /// <summary>Atómico: construye los atributos con el estado previo y luego actualiza el estado con la transacción.</summary>
    ResultadoEstadoTx ProcesarTx(Transaccion tx);
    ContextoLogin ConsultarLogin(string idCliente, long ts, string idDispositivo, string? departamentoIp, IEnumerable<string>? dispositivosConocidosExtra = null);

    void GuardarSesion(SesionIngreso sesion);
    SesionIngreso? ObtenerSesion(string idSesion);
    /// <summary>Guarda el último score de ingreso del cliente (NO es atributo del modelo: solo escala la política).</summary>
    void RegistrarScoreIngreso(string idCliente, double score, DateTime ahoraUtc);
    double? ScoreIngresoVigente(string idCliente, DateTime ahoraUtc, TimeSpan vigencia);
}

/// <summary>
/// Port 1:1 de <c>EstadoEnLinea</c> de training/features.py (mismas ventanas, mismos desempates, misma reputación).
/// Thread-safe con un único candado: cada operación dura microsegundos y el orden de las transacciones importa
/// (cada una ve el estado que dejó la anterior).
/// </summary>
public sealed class EstadoEnLinea(IConstructorAtributos constructor) : IEstadoEnLinea, IVistaEstado
{
    private readonly object _candado = new();
    private readonly Dictionary<string, long> _vinculacion = new();
    private readonly Dictionary<string, SesionIngreso> _sesiones = new();
    private readonly Dictionary<string, (double Score, DateTime Utc)> _scoreIngreso = new();

    // historial por cliente
    private Dictionary<string, int> _nHist = new();
    private Dictionary<string, double> _sumaHist = new();
    private Dictionary<string, List<long>> _montosOrd = new();
    private Dictionary<string, long> _ultimaTx = new();
    // ventanas
    private Dictionary<string, Queue<long>> _tx60 = new();
    private Dictionary<string, Queue<(long Ts, long Monto)>> _salidas24h = new();
    private Dictionary<string, (long Dia, long Acum)> _acumDia = new();
    private Dictionary<string, Queue<long>> _destinosNuevos24h = new();
    private Dictionary<string, Queue<(long Ts, string Remitente, long Monto)>> _entradas7d = new();
    private Dictionary<string, Dictionary<string, int>> _remitentes7d = new();
    private Dictionary<string, int> _destN = new();
    private Dictionary<string, int> _destNuevos = new();
    // eventos de app
    private Dictionary<string, Queue<long>> _fallidos24h = new();
    private Dictionary<string, long> _ultimoRegistro = new();
    private Dictionary<string, long> _ultimoCambioClave = new();
    private Dictionary<string, long> _ultimoCambioSim = new();
    private Dictionary<string, HashSet<string>> _dispositivos = new();
    private Dictionary<string, Dictionary<string, int>> _ipConteo = new();

    public void CargarVinculaciones(IEnumerable<KeyValuePair<string, long>> vinculaciones)
    {
        lock (_candado) foreach (var kv in vinculaciones) _vinculacion[kv.Key] = kv.Value;
    }

    public void Reiniciar()
    {
        lock (_candado)
        {
            _sesiones.Clear(); _scoreIngreso.Clear();
            _nHist = new(); _sumaHist = new(); _montosOrd = new(); _ultimaTx = new();
            _tx60 = new(); _salidas24h = new(); _acumDia = new(); _destinosNuevos24h = new();
            _entradas7d = new(); _remitentes7d = new(); _destN = new(); _destNuevos = new();
            _fallidos24h = new(); _ultimoRegistro = new(); _ultimoCambioClave = new(); _ultimoCambioSim = new();
            _dispositivos = new(); _ipConteo = new();
        }
    }

    // ------------------------------------------------------------------ eventos
    public void ProcesarEvento(EventoApp ev)
    {
        var ts = ev.Ts;
        lock (_candado)
        {
            var cid = ev.IdCliente;
            switch (ev.TipoEvento)
            {
                case TiposEvento.InicioSesion:
                    if (ev.Fallido) Cola(_fallidos24h, cid).Enqueue(ts);
                    else
                    {
                        if (ev.IdDispositivo is not null) Conjunto(_dispositivos, cid).Add(ev.IdDispositivo);
                        SumarIp(cid, ev.DepartamentoIp);
                    }
                    break;
                case TiposEvento.RegistroDispositivo: _ultimoRegistro[cid] = ts; break; // el registro NO cuenta como "visto"
                case TiposEvento.CambioClave: _ultimoCambioClave[cid] = ts; break;
                case TiposEvento.CambioSim: _ultimoCambioSim[cid] = ts; break;
            }
        }
    }

    public ContextoLogin ConsultarLogin(string idCliente, long ts, string idDispositivo, string? departamentoIp, IEnumerable<string>? extra = null)
    {
        lock (_candado)
        {
            _dispositivos.TryGetValue(idCliente, out var vistos);
            var extras = extra?.ToHashSet();
            var tiene = (vistos?.Count ?? 0) > 0 || (extras?.Count ?? 0) > 0;
            var conocido = (vistos?.Contains(idDispositivo) ?? false) || (extras?.Contains(idDispositivo) ?? false);
            var obs = _ipConteo.TryGetValue(idCliente, out var ips) ? ips.Values.Sum() : 0;
            return new ContextoLogin(
                tiene, conocido, FallidosP(idCliente, ts, Tiempo.Hora), FallidosP(idCliente, ts, Tiempo.Dia),
                _ultimoCambioSim.TryGetValue(idCliente, out var s) && ts - s <= 2 * Tiempo.Dia,
                _ultimoCambioClave.TryGetValue(idCliente, out var c) && ts - c <= Tiempo.Dia,
                _ultimoRegistro.TryGetValue(idCliente, out var r) && ts - r <= Tiempo.Dia,
                departamentoIp, IpHabitual(idCliente), obs, (int)(ts % Tiempo.Dia / Tiempo.Hora));
        }
    }

    private void SumarIp(string cid, string? dep)
    {
        if (string.IsNullOrEmpty(dep)) return;
        if (!_ipConteo.TryGetValue(cid, out var d)) _ipConteo[cid] = d = new();
        d[dep] = d.GetValueOrDefault(dep) + 1;
    }

    /// <summary>Moda del departamento de IP; desempate determinista: mayor conteo y luego nombre (orden ordinal).</summary>
    private string? IpHabitual(string cid) =>
        _ipConteo.TryGetValue(cid, out var d) && d.Count > 0
            ? d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key
            : null;

    // ----------------------------------------------------------------- utilidades
    private static Queue<T> Cola<T>(Dictionary<string, Queue<T>> d, string k)
    {
        if (!d.TryGetValue(k, out var q)) d[k] = q = new Queue<T>();
        return q;
    }

    private static HashSet<string> Conjunto(Dictionary<string, HashSet<string>> d, string k)
    {
        if (!d.TryGetValue(k, out var q)) d[k] = q = new HashSet<string>();
        return q;
    }

    private void PurgarEntradas(string dest, long ts)
    {
        if (!_entradas7d.TryGetValue(dest, out var dq)) return;
        var rem = _remitentes7d[dest];
        while (dq.Count > 0 && dq.Peek().Ts < ts - 7 * Tiempo.Dia)
        {
            var (_, r, _) = dq.Dequeue();
            if (--rem[r] <= 0) rem.Remove(r);
        }
    }

    private int FallidosP(string cid, long ts, long ventana)
    {
        if (!_fallidos24h.TryGetValue(cid, out var dq) || dq.Count == 0) return 0;
        while (dq.Count > 0 && dq.Peek() < ts - Tiempo.Dia) dq.Dequeue();
        return dq.Count(t => t >= ts - ventana);
    }

    private int FanInP(string dest, long ts)
    {
        PurgarEntradas(dest, ts);
        return _remitentes7d.TryGetValue(dest, out var r) ? r.Count : 0;
    }

    /// <summary>(n_24h, monto_24h, n_7d, monto_7d) de entradas a la cuenta, solo estado previo.</summary>
    private (int N24, long M24, int N7, long M7) EntradasP(string cuenta, long ts)
    {
        PurgarEntradas(cuenta, ts);
        if (!_entradas7d.TryGetValue(cuenta, out var dq) || dq.Count == 0) return (0, 0, 0, 0);
        var n24 = 0;
        long m24 = 0, m7 = 0;
        foreach (var (t, _, m) in dq)
        {
            m7 += m;
            if (t >= ts - Tiempo.Dia) { n24++; m24 += m; }
        }
        return (n24, m24, dq.Count, m7);
    }

    private long SalidasMonto24hP(string cuenta, long ts)
    {
        if (!_salidas24h.TryGetValue(cuenta, out var dq)) return 0;
        while (dq.Count > 0 && dq.Peek().Ts < ts - Tiempo.Dia) dq.Dequeue();
        long suma = 0;
        foreach (var (_, m) in dq) suma += m;
        return suma;
    }

    // ------------------------------------------------- IVistaEstado (sin candado propio)
    (int N, double Media, long P95, long? UltimaTx) IVistaEstado.Historial(string cid)
    {
        var n = _nHist.GetValueOrDefault(cid);
        long? ult = _ultimaTx.TryGetValue(cid, out var u) ? u : null;
        if (n < 3) return (n, 0, 0, ult);
        var lst = _montosOrd[cid];
        return (n, _sumaHist[cid] / n, lst[(int)(0.95 * (n - 1))], ult);
    }

    int IVistaEstado.ConteoTx60(string cid, long ts)
    {
        if (!_tx60.TryGetValue(cid, out var dq)) return 0;
        while (dq.Count > 0 && dq.Peek() < ts - Tiempo.Hora) dq.Dequeue();
        return dq.Count;
    }

    (int S10, int S60) IVistaEstado.Salidas(string cid, long ts)
    {
        int s10 = 0, s60 = 0;
        if (_salidas24h.TryGetValue(cid, out var dq))
        {
            while (dq.Count > 0 && dq.Peek().Ts < ts - Tiempo.Dia) dq.Dequeue();
            foreach (var (t, _) in dq)
                if (t >= ts - Tiempo.Hora)
                {
                    s60++;
                    if (t >= ts - 10 * Tiempo.Min) s10++;
                }
        }
        return (s10, s60);
    }

    long IVistaEstado.AcumuladoDiaPrevio(string cid, long ts) =>
        _acumDia.TryGetValue(cid, out var ad) && ad.Dia == ts / Tiempo.Dia ? ad.Acum : 0;

    int IVistaEstado.DestinosNuevos24h(string cid, long ts)
    {
        if (!_destinosNuevos24h.TryGetValue(cid, out var dq)) return 0;
        while (dq.Count > 0 && dq.Peek() < ts - Tiempo.Dia) dq.Dequeue();
        return dq.Count;
    }

    int IVistaEstado.Fallidos(string cid, long ts, long ventana) => FallidosP(cid, ts, ventana);
    long? IVistaEstado.UltimoRegistro(string cid) => _ultimoRegistro.TryGetValue(cid, out var t) ? t : null;
    long? IVistaEstado.UltimoCambioClave(string cid) => _ultimoCambioClave.TryGetValue(cid, out var t) ? t : null;
    long? IVistaEstado.UltimoCambioSim(string cid) => _ultimoCambioSim.TryGetValue(cid, out var t) ? t : null;
    bool IVistaEstado.DispositivoVisto(string cid, string disp) => _dispositivos.TryGetValue(cid, out var s) && s.Contains(disp);
    int IVistaEstado.FanIn(string dest, long ts) => FanInP(dest, ts);
    int IVistaEstado.NTxDestino7d(string dest) => _entradas7d.TryGetValue(dest, out var dq) ? dq.Count : 0;
    (int Recibidas, int ConDestinoNuevo) IVistaEstado.HistoricoDestino(string dest) => (_destN.GetValueOrDefault(dest), _destNuevos.GetValueOrDefault(dest));
    (int N24, long M24, int N7, long M7) IVistaEstado.Entradas(string cuenta, long ts) => EntradasP(cuenta, ts);
    long IVistaEstado.SalidasMonto24h(string cuenta, long ts) => SalidasMonto24hP(cuenta, ts);
    double IVistaEstado.Reputacion(string cuenta, string? tipoDestino, long ts) => CalcularReputacion(cuenta, tipoDestino, ts, null);

    /// <summary>
    /// Reputación 0-100 (100 = confiable), fórmula determinista de features.py:
    /// -min(50, 15*(fan-in 7d - 1)) en cuentas Yuma/otras entidades; -25 si antigüedad &lt; 15 d (-10 si &lt; 30 d, cuentas Yuma);
    /// -20 si lo que entra en 7 d sale rápido (salidas 24 h &gt;= 70 % de entradas 7 d). tipoDestino null = cuenta origen.
    /// </summary>
    private double CalcularReputacion(string cuenta, string? tipoDestino, long ts, List<string>? senales)
    {
        double penal = 0;
        var esCuenta = tipoDestino is null or "cuenta_yuma";
        if (tipoDestino is null or "cuenta_yuma" or "otra_entidad")
        {
            var fan = FanInP(cuenta, ts);
            var p = Math.Min(50, 15 * Math.Max(0, fan - 1));
            if (p > 0) senales?.Add($"{fan} remitentes distintos enviaron fondos a la cuenta en 7 días");
            penal += p;
        }
        if (esCuenta && _vinculacion.TryGetValue(cuenta, out var vinc))
        {
            var antig = (ts - vinc) / (double)Tiempo.Dia;
            if (antig < 15) { penal += 25; senales?.Add("cuenta con menos de 15 días de antigüedad"); }
            else if (antig < 30) { penal += 10; senales?.Add("cuenta con menos de 30 días de antigüedad"); }
        }
        if (esCuenta)
        {
            var m7 = EntradasP(cuenta, ts).M7;
            if (m7 > 0 && SalidasMonto24hP(cuenta, ts) >= 0.7 * m7)
            {
                penal += 20;
                senales?.Add("patrón mula: los fondos que entran salen rápidamente");
            }
        }
        return Math.Max(0.0, 100.0 - penal);
    }

    // --------------------------------------------------------------- transacciones
    public ResultadoEstadoTx ProcesarTx(Transaccion tx)
    {
        lock (_candado)
        {
            var atributos = constructor.Construir(tx, this);
            ActualizarTx(tx);

            // Reputación tras la transacción (la que se persiste); el origen es siempre una cuenta Yuma.
            var ts = tx.Ts;
            var sOrigen = new List<string>();
            var origen = new ReputacionCalculada(tx.IdCliente, CalcularReputacion(tx.IdCliente, null, ts, sOrigen), sOrigen);
            ReputacionCalculada? destino = null;
            if (tx.TipoDestino is "cuenta_yuma" or "otra_entidad")
            {
                var sDestino = new List<string>();
                destino = new ReputacionCalculada(tx.IdDestino, CalcularReputacion(tx.IdDestino, tx.TipoDestino, ts, sDestino), sDestino);
            }
            return new ResultadoEstadoTx(atributos, origen, destino);
        }
    }

    private void ActualizarTx(Transaccion tx)
    {
        var cid = tx.IdCliente;
        var ts = tx.Ts;
        var monto = tx.MontoCop;
        var dest = tx.IdDestino;
        _nHist[cid] = _nHist.GetValueOrDefault(cid) + 1;
        _sumaHist[cid] = _sumaHist.GetValueOrDefault(cid) + monto;
        if (!_montosOrd.TryGetValue(cid, out var ord)) _montosOrd[cid] = ord = new List<long>();
        var pos = ord.BinarySearch(monto);
        ord.Insert(pos < 0 ? ~pos : pos, monto);
        _ultimaTx[cid] = ts;
        Cola(_tx60, cid).Enqueue(ts);
        if (tx.EsSalida)
        {
            Cola(_salidas24h, cid).Enqueue((ts, monto));
            var dia = ts / Tiempo.Dia;
            _acumDia[cid] = _acumDia.TryGetValue(cid, out var ad) && ad.Dia == dia ? (dia, ad.Acum + monto) : (dia, monto);
        }
        if (tx.DestinoNuevo == 1) Cola(_destinosNuevos24h, cid).Enqueue(ts);
        PurgarEntradas(dest, ts); // acota la memoria; equivale a purgar en la siguiente consulta (ts no decrece)
        Cola(_entradas7d, dest).Enqueue((ts, cid, monto));
        _destN[dest] = _destN.GetValueOrDefault(dest) + 1;
        _destNuevos[dest] = _destNuevos.GetValueOrDefault(dest) + tx.DestinoNuevo;
        if (!_remitentes7d.TryGetValue(dest, out var rem)) _remitentes7d[dest] = rem = new();
        rem[cid] = rem.GetValueOrDefault(cid) + 1;
        Conjunto(_dispositivos, cid).Add(tx.IdDispositivo);
        SumarIp(cid, tx.DepartamentoIp);
    }

    // ------------------------------------------------------- sesiones / score de ingreso
    public void GuardarSesion(SesionIngreso sesion)
    {
        lock (_candado)
        {
            if (_sesiones.Count > 50_000)
                foreach (var k in _sesiones.Where(s => s.Value.ExpiraUtc < sesion.ExpiraUtc.AddHours(-1)).Select(s => s.Key).ToList())
                    _sesiones.Remove(k);
            _sesiones[sesion.IdSesion] = sesion;
        }
    }

    public SesionIngreso? ObtenerSesion(string idSesion)
    {
        lock (_candado) return _sesiones.GetValueOrDefault(idSesion);
    }

    public void RegistrarScoreIngreso(string idCliente, double score, DateTime ahoraUtc)
    {
        lock (_candado) _scoreIngreso[idCliente] = (score, ahoraUtc);
    }

    public double? ScoreIngresoVigente(string idCliente, DateTime ahoraUtc, TimeSpan vigencia)
    {
        lock (_candado)
            return _scoreIngreso.TryGetValue(idCliente, out var s) && ahoraUtc - s.Utc <= vigencia ? s.Score : null;
    }
}
