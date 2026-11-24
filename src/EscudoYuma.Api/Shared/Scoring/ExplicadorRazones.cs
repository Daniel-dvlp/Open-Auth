using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EscudoYuma.Api.Shared.Scoring;

/// <summary>
/// Razones legibles de una decisión. Port de training/explicacion.py::razones: aproximación ADITIVA de SHAP exportada en
/// explicacion.json. Para cada atributo i: cubeta_i = #cortes_i &lt;= x_i (búsqueda binaria, upper bound) y
/// contrib_i = tabla_i[cubeta_i] (log-odds). Se mencionan los 3 atributos con mayor contribución &gt; umbral (0,15).
/// Singleton: solo lee tablas inmutables. El JSON es la fuente de cortes, tablas y umbral; el texto de las plantillas
/// vive aquí (con tildes y unidades) y reemplaza al del JSON cuando existe.
/// </summary>
public sealed partial class ExplicadorRazones
{
    private readonly string[] _nombres;
    private readonly double[][] _cortes;
    private readonly double[][] _tablas;
    private readonly string[] _plantillas;
    private readonly double _umbralMin;
    private readonly int _maxRazones;

    public ExplicadorRazones(string rutaExplicacionJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(rutaExplicacionJson));
        var r = doc.RootElement;
        _nombres = r.GetProperty("atributos").EnumerateArray().Select(a => a.GetString()!).ToArray();
        if (!_nombres.SequenceEqual(CatalogoAtributos.Nombres))
            throw new InvalidOperationException("El orden de atributos de explicacion.json no coincide con el de C#.");
        _cortes = r.GetProperty("cortes").EnumerateArray().Select(c => c.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToArray();
        _tablas = r.GetProperty("tablas").EnumerateArray().Select(c => c.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToArray();
        _umbralMin = r.GetProperty("umbral_minimo_contribucion").GetDouble();
        _maxRazones = r.GetProperty("max_razones").GetInt32();
        var json = r.GetProperty("plantillas");
        _plantillas = _nombres.Select(n => Plantillas.TryGetValue(n, out var p) ? p : json.GetProperty(n).GetString()!).ToArray();
    }

    /// <summary>Plantillas en español con tildes; <c>{v:.0f}</c>, <c>{v:.1f}</c>, <c>{v:.0%}</c> (con espacio: "96 %") y <c>{v:N0}</c> (miles con punto).</summary>
    private static readonly Dictionary<string, string> Plantillas = new()
    {
        ["destino_nuevo"] = "Destino nuevo para el cliente",
        ["dispositivo_nuevo"] = "Dispositivo registrado en las últimas 24 horas",
        ["cambio_clave_24h"] = "Cambio de clave en las últimas 24 horas",
        ["cambio_sim_48h"] = "Cambio de SIM en las últimas 48 horas",
        ["ip_departamento_distinto"] = "Ubicación distinta a la habitual",
        ["monto_cop"] = "Monto alto (${v:N0} COP)",
        ["log_monto"] = "Monto alto para este tipo de operación",
        ["monto_sobre_saldo"] = "Monto igual al {v:.0%} del saldo",
        ["antiguedad_cuenta_dias"] = "Cuenta muy reciente ({v:.0f} días)",
        ["n_tx_previas"] = "Poco historial del cliente ({v:.0f} operaciones previas)",
        ["monto_vs_media"] = "Monto {v:.1f} veces superior a su promedio",
        ["monto_vs_p95"] = "Monto {v:.1f} veces superior a su percentil 95 histórico",
        ["min_desde_ultima_tx"] = "Actividad tras un periodo inusual de inactividad ({v:N0} min)",
        ["n_tx_60min"] = "Varias operaciones en la última hora ({v:.0f})",
        ["n_salidas_10min"] = "Ráfaga de salidas de dinero ({v:.0f} en 10 min)",
        ["n_salidas_60min"] = "Muchas salidas de dinero en 60 min ({v:.0f})",
        ["acumulado_dia_cop"] = "Acumulado de salidas del día elevado (${v:N0} COP)",
        ["n_destinos_nuevos_24h"] = "Varios destinos nuevos en 24 horas ({v:.0f})",
        ["login_fallidos_1h"] = "Inicios de sesión fallidos en la última hora ({v:.0f})",
        ["login_fallidos_24h"] = "Inicios de sesión fallidos en 24 horas ({v:.0f})",
        ["min_desde_registro_dispositivo"] = "Dispositivo registrado hace solo {v:N0} min",
        ["min_desde_cambio_clave"] = "Cambio de clave hace solo {v:N0} min",
        ["min_desde_cambio_sim"] = "Cambio de SIM hace solo {v:N0} min",
        ["dispositivo_visto_antes"] = "Dispositivo nunca visto para este cliente",
        ["fanin_destino_7d"] = "El destino recibió de {v:.0f} remitentes distintos en 7 días",
        ["n_tx_destino_7d"] = "Destino con muchas operaciones recibidas en 7 días ({v:.0f})",
        ["entradas_n_24h"] = "La cuenta origen recibió fondos hace poco ({v:.0f} entradas en 24 h)",
        ["entradas_monto_24h"] = "La cuenta origen recibió ${v:N0} COP en 24 h",
        ["entradas_n_7d"] = "La cuenta origen recibió fondos de terceros en 7 días ({v:.0f})",
        ["ratio_salida_entradas"] = "Lo que sale supera lo que entró recientemente (razón {v:.1f})",
        ["reputacion_origen"] = "Reputación baja de la cuenta origen ({v:.0f}/100)",
        ["reputacion_destino"] = "Reputación baja de la cuenta destino ({v:.0f}/100)",
        ["hora"] = "Hora inusual ({v:.0f} h)",
        ["es_madrugada"] = "Operación de madrugada",
        ["dest_cuenta_yuma"] = "Transferencia a otra cuenta Yuma",
        ["dest_otra_entidad"] = "Transferencia a otra entidad",
        ["destino_n_previas"] = "Destino con {v:.0f} operaciones recibidas antes",
        ["destino_frac_nuevos"] = "El destino suele ser nuevo para quienes le envían ({v:.0%})",
    };

    /// <summary>Contribución aditiva (log-odds) de cada atributo según la tabla de su cubeta.</summary>
    public double[] Contribuciones(double[] x)
    {
        var c = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            // numpy.searchsorted(cortes, x, side="right"); el entrenamiento cortó sobre float32, por eso se redondea a float32
            c[i] = _tablas[i][ContarMenoresOIguales(_cortes[i], (float)x[i])];
        }
        return c;
    }

    /// <summary>Cantidad de cortes &lt;= x (los cortes vienen ordenados).</summary>
    private static int ContarMenoresOIguales(double[] cortes, double x)
    {
        int lo = 0, hi = cortes.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (cortes[mid] <= x) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Hasta 3 razones (las de mayor contribución &gt; umbral). Lista vacía si ninguna supera el umbral.</summary>
    public IReadOnlyList<string> Explicar(AtributosTransaccion a)
    {
        var contrib = Contribuciones(a.Valores);
        var orden = Enumerable.Range(0, contrib.Length).OrderByDescending(i => contrib[i]).Take(_maxRazones);
        var razones = new List<string>(_maxRazones);
        foreach (var i in orden)
        {
            if (contrib[i] <= _umbralMin) continue;
            var nombre = _nombres[i];
            // Las señales R00x usan el texto concreto de la regla que se disparó (con valores), no el genérico.
            var regla = nombre.Length == 4 && nombre[0] == 'R' ? a.Reglas.FirstOrDefault(r => r.IdRegla == nombre) : null;
            razones.Add(regla is not null ? $"Regla vigente {nombre}: {regla.Razon}" : Formatear(_plantillas[i], a.Valores[i]));
        }
        return razones;
    }

    [GeneratedRegex(@"\{v(?::([^}]*))?\}")]
    private static partial Regex Marcador();

    public static string Formatear(string plantilla, double v) => Marcador().Replace(plantilla, m =>
    {
        var f = m.Groups[1].Value;
        return f switch
        {
            "" => v.ToString("0.##", CultureInfo.InvariantCulture),
            "N0" => v.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.'), // 1.900.000
            ".0%" => (v * 100).ToString("F0", CultureInfo.InvariantCulture) + " %",
            ".0f" => v.ToString("F0", CultureInfo.InvariantCulture),
            ".1f" => v.ToString("F1", CultureInfo.InvariantCulture),
            ".2f" => v.ToString("F2", CultureInfo.InvariantCulture),
            _ => v.ToString("0.##", CultureInfo.InvariantCulture),
        };
    });

    /// <summary>Razón genérica coherente con la decisión, para cuando ningún atributo supera el umbral. Nunca vacía.</summary>
    public static string RazonGenerica(NivelInterno nivel, double puntaje) => nivel switch
    {
        NivelInterno.Aprobar => $"Sin señales de riesgo relevantes (puntaje {puntaje:0.000})",
        NivelInterno.Advertir or NivelInterno.Verificar => $"Combinación de señales de riesgo moderado (puntaje {puntaje:0.000}): se pide verificación adicional",
        NivelInterno.Retener => $"Puntaje de riesgo alto ({puntaje:0.000}): operación retenida para revisión de un analista",
        _ => $"Puntaje de riesgo muy alto ({puntaje:0.000}): operación rechazada",
    };
}
