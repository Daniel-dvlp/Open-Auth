using System.Globalization;
using EscudoYuma.Api.Shared.Datos;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Reglas;
using EscudoYuma.Api.Shared.Scoring;
using Microsoft.ML.OnnxRuntime;

namespace EscudoYuma.Api.Tests;

/// <summary>Ubicación de los datos sintéticos (03_Datos). Variable YUMA_DATOS, igual que training/datos.py.</summary>
public static class RutasDatos
{
    public static string Directorio => Environment.GetEnvironmentVariable("YUMA_DATOS")
        ?? @"C:\Users\Daniel\Documents\Yuma_Pay\Caso_de_estudio_Yuma_Pay\03_Datos";
    public static bool Disponibles => File.Exists(Path.Combine(Directorio, "transacciones.csv"))
        && File.Exists(Path.Combine(Directorio, "eventos_app.csv")) && File.Exists(Path.Combine(Directorio, "clientes.csv"));
    public static string Artefacto(string archivo) => Path.Combine(AppContext.BaseDirectory, "artefactos", archivo);
    /// <summary>Valores de imputación desde banderas (training/artefactos/imputacion.json).</summary>
    public static ImputacionEventos Imputacion => ImputacionEventos.Cargar(Artefacto("imputacion.json"));
}

/// <summary>[Fact] que se salta (con motivo) cuando no hay datos de 03_Datos en la máquina.</summary>
public sealed class FactConDatosAttribute : FactAttribute
{
    public FactConDatosAttribute()
    {
        if (!RutasDatos.Disponibles) Skip = $"Sin datos en {RutasDatos.Directorio} (defina YUMA_DATOS).";
    }
}

public class ParidadAtributosTests
{
    private const double Tolerancia = 1e-6;

    internal static Transaccion LeerTx(Dictionary<string, string> f) => new(
        f["id_transaccion"], f["fecha_hora"], f["id_cliente"], f["tipo_operacion"], f["canal"],
        long.Parse(f["monto_cop"], CultureInfo.InvariantCulture), long.Parse(f["saldo_antes_cop"], CultureInfo.InvariantCulture),
        f["id_destino"], f["tipo_destino"], int.Parse(f["destino_nuevo"], CultureInfo.InvariantCulture), f["id_dispositivo"],
        int.Parse(f["dispositivo_nuevo"], CultureInfo.InvariantCulture), int.Parse(f["cambio_clave_24h"], CultureInfo.InvariantCulture),
        int.Parse(f["cambio_sim_48h"], CultureInfo.InvariantCulture), f["departamento_ip"], int.Parse(f["ip_departamento_distinto"], CultureInfo.InvariantCulture),
        int.Parse(f["antiguedad_cuenta_dias"], CultureInfo.InvariantCulture));

    [Fact]
    public void El_orden_de_atributos_coincide_con_atributos_json_y_ningun_prohibido_entra()
    {
        OnnxModeloAdapter.ValidarAtributos(RutasDatos.Artefacto("atributos.json")); // lanza si difiere
        Assert.Equal(57, CatalogoAtributos.N);
        Assert.Empty(CatalogoAtributos.Nombres.Intersect(CatalogoAtributos.Prohibidos));
    }

    [FactConDatos]
    public void Streaming_en_CSharp_reproduce_los_57_atributos_de_Python_sobre_los_mismos_CSV()
    {
        var dir = RutasDatos.Directorio;
        var txs = LectorCsv.Leer(Path.Combine(dir, "transacciones.csv")).Select(LeerTx)
            .OrderBy(t => t.FechaHora, StringComparer.Ordinal).ThenBy(t => t.IdTransaccion, StringComparer.Ordinal).ToList();
        var eventos = LectorCsv.Leer(Path.Combine(dir, "eventos_app.csv"))
            .Select(f => new EventoApp(f["id_evento"], f["fecha_hora"], f["id_cliente"], f["tipo_evento"], f["id_dispositivo"], f["sistema_operativo"], f["departamento_ip"], f["resultado"]))
            .OrderBy(e => e.FechaHora, StringComparer.Ordinal).ThenBy(e => e.IdEvento, StringComparer.Ordinal).ToList();
        var vinculaciones = LectorCsv.Leer(Path.Combine(dir, "clientes.csv"))
            .Select(f => KeyValuePair.Create(f["id_cliente"], Tiempo.FechaAEpoch(f["fecha_vinculacion"])));

        // Fixture de Python: id -> 57 atributos (float64 con repr completo)
        var esperado = new Dictionary<string, double[]>();
        string[] cabecera = [];
        foreach (var (linea, i) in File.ReadLines(RutasDatos.Artefacto("fixture_atributos.csv")).Select((l, i) => (l, i)))
        {
            var campos = linea.Split(',');
            if (i == 0) { cabecera = campos[1..]; continue; }
            esperado[campos[0]] = campos[1..].Select(c => double.Parse(c, CultureInfo.InvariantCulture)).ToArray();
        }
        Assert.Equal(CatalogoAtributos.Nombres, cabecera);
        Assert.InRange(esperado.Count, 1800, 2200);

        // Mismo flujo que features.py: a igual segundo la transacción va ANTES que los eventos.
        var estado = new EstadoEnLinea(new ConstructorAtributos(new CatalogoReglas(CatalogoReglas.ReglasVigentes()), RutasDatos.Imputacion));
        estado.CargarVinculaciones(vinculaciones);
        var fallas = new List<string>();
        var comparadas = 0;
        var ie = 0;
        foreach (var tx in txs)
        {
            var ts = tx.Ts;
            while (ie < eventos.Count && eventos[ie].Ts < ts) estado.ProcesarEvento(eventos[ie++]);
            var v = estado.ProcesarTx(tx).Atributos.Valores;
            if (!esperado.TryGetValue(tx.IdTransaccion, out var py)) continue;
            comparadas++;
            for (var k = 0; k < py.Length; k++)
                if (Math.Abs(v[k] - py[k]) > Tolerancia && fallas.Count < 15)
                    fallas.Add($"{tx.IdTransaccion} {CatalogoAtributos.Nombres[k]}: C#={v[k]:R} Python={py[k]:R}");
        }
        Assert.Equal(esperado.Count, comparadas);
        Assert.True(fallas.Count == 0, "Atributos distintos (tolerancia 1e-6):\n" + string.Join("\n", fallas));
    }

    [Fact]
    public void El_fixture_cubre_destinos_con_fanin_y_periodos_con_eventos()
    {
        var filas = File.ReadLines(RutasDatos.Artefacto("fixture_atributos.csv")).Skip(1).Select(l => l.Split(',')).ToList();
        int Col(string n) => Array.IndexOf(CatalogoAtributos.Nombres, n) + 1;
        Assert.True(filas.Count(f => double.Parse(f[Col("fanin_destino_7d")], CultureInfo.InvariantCulture) >= 2) >= 100);
        Assert.True(filas.Count(f => double.Parse(f[Col("login_fallidos_24h")], CultureInfo.InvariantCulture) > 0) >= 100);
        Assert.True(filas.Count(f => double.Parse(f[Col("min_desde_cambio_sim")], CultureInfo.InvariantCulture) < 10080) >= 50);
    }
}

public class ParidadOnnxTests
{
    [Fact]
    public void El_modelo_expone_input_float_N_57_y_probabilities_N_2()
    {
        using var sesion = new InferenceSession(RutasDatos.Artefacto("modelo_tx.onnx"));
        var entrada = Assert.Single(sesion.InputMetadata);
        Assert.Equal("input", entrada.Key);
        Assert.Equal(typeof(float), entrada.Value.ElementType);
        Assert.Equal(57, entrada.Value.Dimensions[1]);
        var salida = sesion.OutputMetadata["probabilities"];
        Assert.Equal(typeof(float), salida.ElementType);
        Assert.Equal(2, salida.Dimensions[1]);
    }

    [Fact]
    public void Score_y_decision_de_CSharp_coinciden_con_paridad_csv_de_Python()
    {
        var art = new ArtefactosOptions { Directorio = Path.Combine(AppContext.BaseDirectory, "artefactos") };
        using var modelo = new OnnxModeloAdapter(art.RutaModelo, "test", art.Ruta("atributos.json"));
        var politica = new PoliticaDecision(UmbralesDecision.Cargar(art.Ruta("umbrales.json")));
        var lineas = File.ReadLines(RutasDatos.Artefacto("paridad.csv")).ToList();
        var cab = lineas[0].Split(',');
        Assert.Equal(CatalogoAtributos.Nombres, cab[1..58]);
        Assert.Equal(["score_python", "decision_python"], cab[58..]);

        var nombres = new[] { "aprobar", "desafiar", "retener", "rechazar" };
        double maxDif = 0;
        var n = 0;
        foreach (var linea in lineas.Skip(1))
        {
            var c = linea.Split(',');
            var v = c[1..58].Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            var scorePy = double.Parse(c[58], CultureInfo.InvariantCulture);
            var decisionPy = nombres[int.Parse(c[59], CultureInfo.InvariantCulture)];

            var attrs = new AtributosTransaccion(v, []);
            var p = modelo.Predecir(attrs);
            maxDif = Math.Max(maxDif, Math.Abs(p - scorePy));
            Assert.True(Math.Abs(p - scorePy) < 1e-4, $"{c[0]}: ONNX C#={p:R} Python={scorePy:R}");
            var decision = PoliticaDecision.Mapear(politica.Decidir(p, ContextoPolitica.Desde(attrs, null, false)).Nivel);
            Assert.Equal(decisionPy, decision);
            n++;
        }
        Assert.Equal(200, n);
        Assert.True(maxDif < 1e-4);
    }
}
