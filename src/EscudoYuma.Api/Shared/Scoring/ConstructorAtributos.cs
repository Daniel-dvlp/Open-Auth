using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Reglas;

namespace EscudoYuma.Api.Shared.Scoring;

/// <summary>
/// Catálogo de atributos del modelo: MISMO orden que training/artefactos/atributos.json (se valida al arrancar).
/// </summary>
public static class CatalogoAtributos
{
    public static readonly string[] Nombres =
    [
        "destino_nuevo", "dispositivo_nuevo", "cambio_clave_24h", "cambio_sim_48h", "ip_departamento_distinto",
        "monto_cop", "log_monto", "monto_sobre_saldo",
        "op_transferencia_inmediata", "op_pago_qr", "op_retiro_corresponsal", "op_retiro_cajero",
        "op_pago_servicios", "op_recarga_celular", "op_compra_tarjeta_virtual",
        "dest_cuenta_yuma", "dest_otra_entidad",
        "hora", "es_madrugada", "antiguedad_cuenta_dias",
        "n_tx_previas", "monto_vs_media", "monto_vs_p95", "min_desde_ultima_tx",
        "n_tx_60min", "n_salidas_10min", "n_salidas_60min", "acumulado_dia_cop", "n_destinos_nuevos_24h",
        "login_fallidos_1h", "login_fallidos_24h",
        "min_desde_registro_dispositivo", "min_desde_cambio_clave", "min_desde_cambio_sim", "dispositivo_visto_antes",
        "fanin_destino_7d", "n_tx_destino_7d", "destino_n_previas", "destino_frac_nuevos",
        "entradas_n_24h", "entradas_monto_24h", "entradas_n_7d", "ratio_salida_entradas",
        "reputacion_origen", "reputacion_destino",
        "R001", "R002", "R003", "R004", "R005", "R006", "R007", "R008", "R009", "R010", "R011", "R012",
    ];

    public static int N => Nombres.Length;
    private static readonly Dictionary<string, int> Indices = Nombres.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
    public static int Indice(string nombre) => Indices[nombre];

    /// <summary>Atributos que NUNCA pueden entrar al modelo (auditoría de sesgo / etiquetas); se verifican en pruebas.</summary>
    public static readonly string[] Prohibidos =
        ["departamento", "rango_edad", "tipo_cliente", "es_fraude", "modalidad_fraude", "cuenta_mula_confirmada", "departamento_ip", "id_cliente", "id_destino"];

    public static readonly int IdxReglaPrimera = Indice("R001");
}

/// <summary>
/// Valores de imputación desde banderas (training/artefactos/imputacion.json, calculados en entrenamiento: mediana de minutos en fraudes
/// de entrenamiento con la bandera y el evento presentes). Se usan cuando la bandera de la transacción dice que el evento ocurrió dentro de su
/// ventana pero el estado no tiene un evento consistente (feed de eventos caído o tardío). Ver training/features.py.
/// </summary>
public sealed record ImputacionEventos(double MinDesdeCambioSim, double MinDesdeCambioClave, double MinDesdeRegistroDispositivo)
{
    public const double VentanaSimMin = 48 * 60;       // cambio_sim_48h
    public const double VentanaClaveMin = 24 * 60;     // cambio_clave_24h
    public const double VentanaRegistroMin = 24 * 60;  // dispositivo_nuevo

    public static ImputacionEventos Cargar(string rutaJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rutaJson));
        var r = doc.RootElement;
        return new ImputacionEventos(r.GetProperty("min_desde_cambio_sim").GetDouble(), r.GetProperty("min_desde_cambio_clave").GetDouble(),
            r.GetProperty("min_desde_registro_dispositivo").GetDouble());
    }
}

/// <summary>Vector de atributos que consume el modelo (orden de <see cref="CatalogoAtributos.Nombres"/>) y las reglas que se dispararon.</summary>
public sealed class AtributosTransaccion(double[] valores, IReadOnlyList<ReglaDisparada> reglas)
{
    public double[] Valores { get; } = valores;
    public IReadOnlyList<ReglaDisparada> Reglas { get; } = reglas;
    public double this[string nombre] => Valores[CatalogoAtributos.Indice(nombre)];
}

public interface IConstructorAtributos
{
    /// <summary>Atributos de la transacción con SOLO el estado previo (la vista se lee antes de actualizarse con la transacción).</summary>
    AtributosTransaccion Construir(Transaccion t, IVistaEstado estado);
}

/// <summary>
/// Port 1:1 de <c>EstadoEnLinea.atributos_tx</c> de training/features.py. Singleton sin estado propio.
/// Las 12 reglas vigentes (Strategy: <see cref="CatalogoReglas"/>) entran como señales 0/1 al final del vector.
/// </summary>
public sealed class ConstructorAtributos(CatalogoReglas reglas, ImputacionEventos imputacion) : IConstructorAtributos
{
    private static readonly string[] TiposOp = Transaccion.Tipos;

    public AtributosTransaccion Construir(Transaccion t, IVistaEstado e)
    {
        var cid = t.IdCliente;
        var ts = t.Ts;
        var op = t.TipoOperacion;
        double monto = t.MontoCop;
        double saldo = t.SaldoAntesCop;
        var dest = t.IdDestino;
        var tdest = t.TipoDestino;
        var hora = (int)(ts % Tiempo.Dia / Tiempo.Hora);
        var esSalida = t.EsSalida;

        // histórico del cliente
        var (n, media, p95, ult) = e.Historial(cid);
        double vsMedia = 1.0, vsP95 = 1.0;
        if (n >= 3)
        {
            vsMedia = monto / Math.Max(media, 1.0);
            vsP95 = monto / Math.Max(p95, 1);
        }
        var minUlt = ult is { } u ? Math.Min((ts - u) / (double)Tiempo.Min, Tiempo.TopeMinSinEvento) : Tiempo.TopeMinSinEvento;

        // velocidad (estado previo + la tx actual cuando la regla vigente la incluye)
        var n60 = e.ConteoTx60(cid, ts);
        var (s10, s60) = e.Salidas(cid, ts);
        var acumPrev = e.AcumuladoDiaPrevio(cid, ts);
        if (esSalida) { s10++; s60++; }
        var acum = acumPrev + (esSalida ? t.MontoCop : 0);
        var nDn = e.DestinosNuevos24h(cid, ts);

        // eventos de la app
        var f1 = e.Fallidos(cid, ts, Tiempo.Hora);
        var f24 = e.Fallidos(cid, ts, Tiempo.Dia);
        double Minutos(long? t0) => t0 is { } x ? Math.Min((ts - x) / (double)Tiempo.Min, Tiempo.TopeMinSinEvento) : Tiempo.TopeMinSinEvento;
        var vis = e.DispositivoVisto(cid, t.IdDispositivo) ? 1.0 : 0.0;
        var mReg = Minutos(e.UltimoRegistro(cid));
        var mClave = Minutos(e.UltimoCambioClave(cid));
        var mSim = Minutos(e.UltimoCambioSim(cid));
        // Imputación desde banderas: la bandera de la tx manda cuando el estado no tiene un evento consistente en la ventana
        // (mismo orden y reglas que features.py::atributos_tx).
        if (t.CambioSim48h == 1 && mSim > ImputacionEventos.VentanaSimMin) mSim = imputacion.MinDesdeCambioSim;
        if (t.CambioClave24h == 1 && mClave > ImputacionEventos.VentanaClaveMin) mClave = imputacion.MinDesdeCambioClave;
        if (t.DispositivoNuevo == 1 && mReg > ImputacionEventos.VentanaRegistroMin) { mReg = imputacion.MinDesdeRegistroDispositivo; vis = 0.0; }

        // red de cuentas
        var fan = e.FanIn(dest, ts);
        var nTxDest = e.NTxDestino7d(dest);
        var (dnPrev, dnNuevos) = e.HistoricoDestino(dest);
        // proporción suavizada (prior 0.25 con peso 4) de remitentes para los que el destino era nuevo
        var fracNuevos = (dnNuevos + 1.0) / (dnPrev + 4.0);
        var (eN24, eM24, eN7, eM7) = e.Entradas(cid, ts);
        double sal24 = e.SalidasMonto24h(cid, ts) + (esSalida ? t.MontoCop : 0);
        var ratio = eM7 > 0 ? Math.Min(sal24 / eM7, 10.0) : esSalida ? 10.0 : 0.0;

        var repO = e.Reputacion(cid, null, ts);
        var repD = e.Reputacion(dest, tdest, ts);

        var disparadas = reglas.Evaluar(new ContextoRegla(t, hora, s60, acum));

        var v = new double[CatalogoAtributos.N];
        var k = 0;
        v[k++] = t.DestinoNuevo; v[k++] = t.DispositivoNuevo; v[k++] = t.CambioClave24h; v[k++] = t.CambioSim48h; v[k++] = t.IpDepartamentoDistinto;
        v[k++] = monto; v[k++] = Math.Log(1.0 + monto); v[k++] = Math.Min(monto / Math.Max(saldo, 1), 5.0);
        foreach (var tipo in TiposOp) v[k++] = op == tipo ? 1 : 0;
        v[k++] = tdest == "cuenta_yuma" ? 1 : 0; v[k++] = tdest == "otra_entidad" ? 1 : 0;
        v[k++] = hora; v[k++] = hora <= 4 ? 1 : 0; v[k++] = t.AntiguedadCuentaDias;
        v[k++] = n; v[k++] = vsMedia; v[k++] = vsP95; v[k++] = minUlt;
        v[k++] = n60; v[k++] = s10; v[k++] = s60; v[k++] = acum; v[k++] = nDn;
        v[k++] = f1; v[k++] = f24; v[k++] = mReg; v[k++] = mClave; v[k++] = mSim; v[k++] = vis;
        v[k++] = fan; v[k++] = nTxDest; v[k++] = dnPrev; v[k++] = fracNuevos; v[k++] = eN24; v[k++] = eM24; v[k++] = eN7; v[k++] = ratio;
        v[k++] = repO; v[k++] = repD;
        foreach (var d in disparadas) v[CatalogoAtributos.IdxReglaPrimera + int.Parse(d.IdRegla.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture) - 1] = 1;
        return new AtributosTransaccion(v, disparadas);
    }
}
