using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EscudoYuma.Api.Shared.Scoring;

public enum TipoModelo { Transaccion }

public interface IModeloRiesgo
{
    string Version { get; }
    /// <summary>Probabilidad de fraude en [0,1].</summary>
    double Predecir(AtributosTransaccion atributos);
}

/// <summary>Ubicación de los artefactos de entrenamiento (modelo_tx.onnx, atributos.json, umbrales.json, explicacion.json).</summary>
public sealed class ArtefactosOptions
{
    /// <summary>Carpeta con los artefactos; por defecto <c>artefactos/</c> junto al ejecutable (se copian al compilar).</summary>
    public string Directorio { get; set; } = Path.Combine(AppContext.BaseDirectory, "artefactos");
    public string RutaOnnx { get; set; } = "";
    public string Version { get; set; } = "xgb-onnx-1";

    public string Ruta(string archivo) => Path.Combine(Directorio, archivo);
    public string RutaModelo => string.IsNullOrWhiteSpace(RutaOnnx) ? Ruta("modelo_tx.onnx") : RutaOnnx;

    public static ArtefactosOptions Desde(IConfiguration cfg)
    {
        var o = new ArtefactosOptions();
        if (cfg["Modelo:DirectorioArtefactos"] is { Length: > 0 } d) o.Directorio = d;
        if (cfg["Modelo:RutaOnnx"] is { Length: > 0 } r) o.RutaOnnx = r;
        if (cfg["Modelo:Version"] is { Length: > 0 } v) o.Version = v;
        return o;
    }
}

/// <summary>
/// ADAPTER del modelo ONNX (XGBoost exportado por training/train.py): <see cref="AtributosTransaccion"/> -> tensor float32 [1, 57]
/// y salida <c>probabilities[:, 1]</c> (clase fraude). Crea UNA sola <see cref="InferenceSession"/> (thread-safe y costosa de crear);
/// la factory la mantiene como Singleton. Valida al arrancar que el contrato del modelo coincide con atributos.json.
/// </summary>
public sealed class OnnxModeloAdapter : IModeloRiesgo, IDisposable
{
    public const string NombreSalidaProbabilidad = "probabilities";
    private readonly InferenceSession _sesion;
    private readonly string _entrada;
    private readonly string[] _salidas = [NombreSalidaProbabilidad];

    public OnnxModeloAdapter(string rutaModelo, string version, string rutaAtributosJson)
    {
        if (!File.Exists(rutaModelo)) throw new FileNotFoundException($"No se encontró el modelo ONNX: {rutaModelo}", rutaModelo);
        ValidarAtributos(rutaAtributosJson);
        Version = version;
        // Sin espera activa en los hilos intra-op: por defecto ONNX Runtime los deja girando (spin) tras cada Run y, con un hilo por núcleo,
        // quemaban ~10 núcleos bajo carga sin acelerar nada (un árbol XGBoost sobre 57 atributos tarda microsegundos). No se fija el número de
        // hilos: con 1 hilo la suma de las hojas cambia en el último decimal y un caso límite del replay cruza el umbral (95,6 % -> 95,54 %).
        using var opciones = new Microsoft.ML.OnnxRuntime.SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        opciones.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        _sesion = new InferenceSession(rutaModelo, opciones);

        // Contrato: una entrada float [N, 57] y una salida de probabilidad float [N, 2].
        var entrada = _sesion.InputMetadata.Single();
        if (entrada.Value.ElementType != typeof(float) || entrada.Value.Dimensions.Length != 2 || entrada.Value.Dimensions[1] != CatalogoAtributos.N)
            throw new InvalidOperationException($"El modelo espera {entrada.Key} {string.Join("x", entrada.Value.Dimensions)} {entrada.Value.ElementType.Name}; se requiere float [N,{CatalogoAtributos.N}].");
        _entrada = entrada.Key;
        if (!_sesion.OutputMetadata.TryGetValue(NombreSalidaProbabilidad, out var salida) || salida.Dimensions.Length != 2 || salida.Dimensions[1] != 2)
            throw new InvalidOperationException($"El modelo no expone la salida '{NombreSalidaProbabilidad}' float [N,2].");
    }

    public string Version { get; }

    public double Predecir(AtributosTransaccion atributos)
    {
        var datos = new float[CatalogoAtributos.N];
        for (var i = 0; i < datos.Length; i++) datos[i] = (float)atributos.Valores[i]; // el entrenamiento usó float32
        var tensor = new DenseTensor<float>(datos, [1, CatalogoAtributos.N]);
        using var resultado = _sesion.Run([NamedOnnxValue.CreateFromTensor(_entrada, tensor)], _salidas);
        return resultado.First().AsTensor<float>()[0, 1];
    }

    /// <summary>Falla al arrancar si el orden/nombre de atributos de C# difiere del de entrenamiento (atributos.json).</summary>
    public static void ValidarAtributos(string rutaJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(rutaJson));
        var esperados = doc.RootElement.GetProperty("atributos").EnumerateArray().Select(a => a.GetString()!).ToArray();
        if (!esperados.SequenceEqual(CatalogoAtributos.Nombres))
            throw new InvalidOperationException("El orden de atributos de C# no coincide con training/artefactos/atributos.json.");
    }

    public void Dispose() => _sesion.Dispose();
}

/// <summary>Factory: entrega el modelo por tipo y versión.</summary>
public interface IModeloRiesgoFactory
{
    IModeloRiesgo Crear(TipoModelo tipo, string? version = null);
}

/// <summary>
/// Singleton: los modelos son costosos, se crean una vez y se reutilizan. Si el modelo no carga se ABORTA el arranque:
/// degradar a un modelo de relleno aprobaría todo en silencio.
/// </summary>
public sealed class ModeloRiesgoFactory : IModeloRiesgoFactory, IDisposable
{
    private readonly Dictionary<(TipoModelo, string), IModeloRiesgo> _modelos = new();
    private readonly Dictionary<TipoModelo, string> _vigente = new();

    public ModeloRiesgoFactory(ArtefactosOptions artefactos, ILogger<ModeloRiesgoFactory> log)
    {
        var onnx = new OnnxModeloAdapter(artefactos.RutaModelo, artefactos.Version, artefactos.Ruta("atributos.json"));
        Registrar(TipoModelo.Transaccion, onnx);
        log.LogInformation("Modelo ONNX {Version} cargado desde {Ruta}.", onnx.Version, artefactos.RutaModelo);
    }

    private void Registrar(TipoModelo tipo, IModeloRiesgo modelo)
    {
        _modelos[(tipo, modelo.Version)] = modelo;
        _vigente[tipo] = modelo.Version;
    }

    public IModeloRiesgo Crear(TipoModelo tipo, string? version = null)
    {
        version ??= _vigente[tipo];
        return _modelos.TryGetValue((tipo, version), out var m)
            ? m
            : throw new KeyNotFoundException($"Modelo {tipo}/{version} no registrado.");
    }

    public void Dispose()
    {
        foreach (var m in _modelos.Values.OfType<IDisposable>()) m.Dispose();
    }
}
