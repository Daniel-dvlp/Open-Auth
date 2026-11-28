using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Reglas;
using EscudoYuma.Api.Shared.Scoring;

namespace EscudoYuma.Api.Tests;

/// <summary>Robustez ante un feed de eventos caído: imputación desde banderas (atributos) y piso de reglas de seguridad (política).</summary>
public class FeedCaidoEndpointTests(FabricaApi api) : IClassFixture<FabricaApi>
{
    private static async Task<JsonElement> EvaluarAsync(HttpClient c, Transaccion tx)
    {
        var resp = await c.PostAsJsonAsync("/v1/score/transaccion", tx, FabricaApi.Snake);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Transaccion TxCasoDescrito(string cliente) =>
        FabricaApi.Tx(cliente, monto: 475_000, saldo: 500_000, riesgosa: true) with { AntiguedadCuentaDias = 200 };

    private static List<string> Razones(JsonElement j) => j.GetProperty("razones").EnumerateArray().Select(r => r.GetString()!).ToList();

    [Fact]
    public async Task Caso_descrito_con_estado_vacio_ya_no_se_aprueba_y_va_a_un_analista()
    {
        // Dispositivo nuevo + cambio de SIM + cambio de clave + 95 % del saldo a un destino nuevo, SIN ningún evento en el estado.
        var servicio = await api.ClienteAutenticadoAsync("SVC-FC1", Roles.Servicio);
        // Debe terminar en retener. Puede llegar ahí por el modelo (gracias a la imputación) o por el piso de reglas;
        // si fue el piso, la razón debe decirlo. Si fue el modelo, el puntaje debe superar el umbral de retención.
        var j = await EvaluarAsync(servicio, TxCasoDescrito("C-FC-VACIO"));
        Assert.Equal("retener", j.GetProperty("decision").GetString());
        var porPiso = Razones(j).Any(r => r.StartsWith("Regla de seguridad", StringComparison.Ordinal));
        if (!porPiso)
        {
            var art = new ArtefactosOptions { Directorio = Path.Combine(AppContext.BaseDirectory, "artefactos") };
            var umbrales = UmbralesDecision.Cargar(art.Ruta("umbrales.json"));
            Assert.True(j.GetProperty("puntaje").GetDouble() >= umbrales.Retener - umbrales.Tolerancia);
        }
    }

    [Fact]
    public void Con_estado_vacio_el_modelo_solo_ya_desafia_la_tx_riesgosa_gracias_a_la_imputacion()
    {
        var art = new ArtefactosOptions { Directorio = Path.Combine(AppContext.BaseDirectory, "artefactos") };
        using var modelo = new OnnxModeloAdapter(art.RutaModelo, "test", art.Ruta("atributos.json"));
        var umbrales = UmbralesDecision.Cargar(art.Ruta("umbrales.json"));
        var constructor = new ConstructorAtributos(new CatalogoReglas(CatalogoReglas.ReglasVigentes()), RutasDatos.Imputacion);
        var estado = new EstadoEnLinea(constructor);
        var p = modelo.Predecir(estado.ProcesarTx(TxCasoDescrito("C-FC-SOLO-MODELO")).Atributos);
        Assert.True(p >= umbrales.Desafiar, $"p = {p:0.000}: el modelo solo debería desafiar (antes de la imputación daba ~0,08).");
    }

    [Fact]
    public async Task Con_eventos_coherentes_sigue_reteniendo_por_puntaje_del_modelo()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-FC2", Roles.Servicio);
        await FabricaApi.PrepararRiesgoAsync(servicio, "C-FC-EVENTOS");
        var j = await EvaluarAsync(servicio, FabricaApi.TxRetener("C-FC-EVENTOS"));
        Assert.Equal("retener", j.GetProperty("decision").GetString());
        Assert.True(j.GetProperty("puntaje").GetDouble() >= 0.9709, "con eventos reales el modelo mismo retiene (p >= umbral de retención)");
    }

    [Fact]
    public async Task Un_pago_pequeno_sin_banderas_se_aprueba_sin_razon_de_seguridad()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-FC3", Roles.Servicio);
        var j = await EvaluarAsync(servicio, FabricaApi.Tx("C-FC-QR", monto: 20_000));
        Assert.Equal("aprobar", j.GetProperty("decision").GetString());
        Assert.DoesNotContain(Razones(j), r => r.StartsWith("Regla de seguridad", StringComparison.Ordinal));
    }
}

public class ImputacionYParidadSinEventosTests
{
    private static readonly ImputacionEventos Imp = RutasDatos.Imputacion;

    private static EstadoEnLinea Estado() => new(new ConstructorAtributos(new CatalogoReglas(CatalogoReglas.ReglasVigentes()), Imp));

    [Fact]
    public void Sin_eventos_las_banderas_imputan_los_atributos_derivados_y_el_dispositivo_no_es_visto()
    {
        var a = Estado().ProcesarTx(FabricaApi.TxRetener("C-IMP-1")).Atributos;
        Assert.Equal(Imp.MinDesdeCambioSim, a["min_desde_cambio_sim"], 6);
        Assert.Equal(Imp.MinDesdeCambioClave, a["min_desde_cambio_clave"], 6);
        Assert.Equal(Imp.MinDesdeRegistroDispositivo, a["min_desde_registro_dispositivo"], 6);
        Assert.Equal(0.0, a["dispositivo_visto_antes"]);
        Assert.All([Imp.MinDesdeCambioSim, Imp.MinDesdeCambioClave, Imp.MinDesdeRegistroDispositivo],
            m => Assert.InRange(m, 0, Tiempo.TopeMinSinEvento - 1)); // valores dentro de la ventana, nunca "sin evento"
    }

    [Fact]
    public void Un_evento_consistente_dentro_de_la_ventana_manda_sobre_la_imputacion_y_uno_viejo_se_imputa()
    {
        var tx = FabricaApi.TxRetener("C-IMP-2"); // 2026-07-15 14:05
        var e = Estado();
        e.ProcesarEvento(new EventoApp("E1", "2026-07-15 13:55:00", "C-IMP-2", TiposEvento.CambioSim, "DISP-1"));
        Assert.Equal(10.0, e.ProcesarTx(tx).Atributos["min_desde_cambio_sim"], 6);

        var e2 = Estado();
        e2.ProcesarEvento(new EventoApp("E2", "2026-07-12 14:05:00", "C-IMP-2", TiposEvento.CambioSim, "DISP-1")); // 3 días: fuera de 48 h
        Assert.Equal(Imp.MinDesdeCambioSim, e2.ProcesarTx(tx).Atributos["min_desde_cambio_sim"], 6);
    }

    [Fact]
    public void Sin_banderas_no_se_imputa_nada()
    {
        var a = Estado().ProcesarTx(FabricaApi.Tx("C-IMP-3")).Atributos;
        Assert.Equal(Tiempo.TopeMinSinEvento, a["min_desde_cambio_sim"]);
        Assert.Equal(Tiempo.TopeMinSinEvento, a["min_desde_cambio_clave"]);
        Assert.Equal(Tiempo.TopeMinSinEvento, a["min_desde_registro_dispositivo"]);
    }

    [FactConDatos]
    public void Streaming_sin_eventos_en_CSharp_reproduce_los_57_atributos_imputados_de_Python()
    {
        var dir = RutasDatos.Directorio;
        var txs = LectorCsv.Leer(Path.Combine(dir, "transacciones.csv")).Select(ParidadAtributosTests.LeerTx)
            .OrderBy(t => t.FechaHora, StringComparer.Ordinal).ThenBy(t => t.IdTransaccion, StringComparer.Ordinal).ToList();
        var vinculaciones = LectorCsv.Leer(Path.Combine(dir, "clientes.csv"))
            .Select(f => KeyValuePair.Create(f["id_cliente"], Tiempo.FechaAEpoch(f["fecha_vinculacion"])));

        var esperado = new Dictionary<string, double[]>();
        foreach (var linea in File.ReadLines(RutasDatos.Artefacto("fixture_atributos_sin_eventos.csv")).Skip(1))
        {
            var c = linea.Split(',');
            esperado[c[0]] = c[1..].Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }
        Assert.InRange(esperado.Count, 500, 700);

        var estado = Estado();
        estado.CargarVinculaciones(vinculaciones);
        var fallas = new List<string>();
        var comparadas = 0;
        foreach (var tx in txs) // SIN procesar ni un solo evento
        {
            var v = estado.ProcesarTx(tx).Atributos.Valores;
            if (!esperado.TryGetValue(tx.IdTransaccion, out var py)) continue;
            comparadas++;
            for (var k = 0; k < py.Length; k++)
                if (Math.Abs(v[k] - py[k]) > 1e-6 && fallas.Count < 15)
                    fallas.Add($"{tx.IdTransaccion} {CatalogoAtributos.Nombres[k]}: C#={v[k]:R} Python={py[k]:R}");
        }
        Assert.Equal(esperado.Count, comparadas);
        Assert.True(fallas.Count == 0, "Atributos distintos (tolerancia 1e-6):\n" + string.Join("\n", fallas));
    }
}

public class PisoReglasSeguridadTests
{
    private static readonly PoliticaDecision Politica = new(UmbralesDecision.Cargar(RutasDatos.Artefacto("umbrales.json")));

    /// <summary>Contexto sin señales de riesgo del modelo: solo cuentan las señales de la propia transacción.</summary>
    private static ContextoPolitica Ctx(bool dispNuevo = false, bool simOClave = false, bool salida = false, bool transf = false,
        bool destinoNuevo = false, double sobreSaldo = 0.1, double monto = 500_000, double rep = 100, double fanin = 0, bool bloqueada = false) =>
        new(fanin, rep, destinoNuevo, sobreSaldo, false, null, bloqueada, dispNuevo, simOClave, salida, transf, monto);

    [Fact]
    public void A_dispositivo_nuevo_con_SIM_o_clave_en_una_salida_desafia_con_razon_de_seguridad()
    {
        var r = Politica.Decidir(0.0, Ctx(dispNuevo: true, simOClave: true, salida: true));
        Assert.Equal(NivelInterno.Verificar, r.Nivel);
        Assert.Equal(PisoSeguridad.DispositivoSimClave, r.Piso);
        Assert.StartsWith("Regla de seguridad", PoliticaDecision.MotivoPiso(r.Piso));
        Assert.Equal(NivelInterno.Aprobar, Politica.Decidir(0.0, Ctx(dispNuevo: true, simOClave: true, salida: false)).Nivel); // pago, no salida
        Assert.Equal(NivelInterno.Aprobar, Politica.Decidir(0.0, Ctx(dispNuevo: true, simOClave: false, salida: true)).Nivel);
        Assert.Equal(NivelInterno.Aprobar, Politica.Decidir(0.0, Ctx(dispNuevo: false, simOClave: true, salida: true)).Nivel);
    }

    [Fact]
    public void A_con_vaciado_a_destino_nuevo_retiene_para_un_analista_sin_rechazar()
    {
        var r = Politica.Decidir(0.0, Ctx(dispNuevo: true, simOClave: true, salida: true, transf: true, destinoNuevo: true, sobreSaldo: 0.9));
        Assert.Equal(NivelInterno.Retener, r.Nivel);   // nunca Rechazar: el bloqueo de cuenta es decisión del analista
        Assert.Equal(PisoSeguridad.DispositivoSimClaveVaciado, r.Piso);
        Assert.Equal("retener", PoliticaDecision.Mapear(r.Nivel));
        // 0,8999995 entra por la tolerancia (la matriz de Python es float32); 0,89 no
        Assert.Equal(NivelInterno.Retener, Politica.Decidir(0.0, Ctx(true, true, true, true, true, 0.8999995)).Nivel);
        Assert.Equal(NivelInterno.Verificar, Politica.Decidir(0.0, Ctx(true, true, true, true, true, 0.89)).Nivel);
    }

    [Fact]
    public void B_vaciado_de_transferencia_a_destino_nuevo_advierte()
    {
        var r = Politica.Decidir(0.0, Ctx(salida: true, transf: true, destinoNuevo: true, sobreSaldo: 0.95, monto: 100_000));
        Assert.Equal(NivelInterno.Advertir, r.Nivel);
        Assert.Equal(PisoSeguridad.Vaciado, r.Piso);
        Assert.Equal("desafiar", PoliticaDecision.Mapear(r.Nivel));
        Assert.Equal(NivelInterno.Aprobar, Politica.Decidir(0.0, Ctx(salida: true, transf: true, destinoNuevo: true, sobreSaldo: 0.95, monto: 99_999)).Nivel);
        Assert.Equal(NivelInterno.Aprobar, Politica.Decidir(0.0, Ctx(salida: true, transf: false, destinoNuevo: true, sobreSaldo: 0.95)).Nivel);
        Assert.Equal(NivelInterno.Aprobar, Politica.Decidir(0.0, Ctx(salida: true, transf: true, destinoNuevo: false, sobreSaldo: 0.95)).Nivel);
    }

    [Fact]
    public void C_destino_con_reputacion_baja_y_fanin_desafia()
    {
        var r = Politica.Decidir(0.0, Ctx(rep: 30, fanin: 3));
        Assert.Equal(NivelInterno.Verificar, r.Nivel);
        Assert.Equal(PisoSeguridad.DestinoReputacionBaja, r.Piso);
        Assert.Equal(NivelInterno.Aprobar, Politica.Decidir(0.0, Ctx(rep: 31, fanin: 3)).Nivel);
        Assert.Equal(NivelInterno.Aprobar, Politica.Decidir(0.0, Ctx(rep: 30, fanin: 2)).Nivel);
    }

    [Fact]
    public void El_piso_solo_sube_la_decision_del_modelo_nunca_la_baja_ni_toca_el_bloqueo()
    {
        // El modelo ya retiene: se conserva y no se marca el piso.
        var retiene = Politica.Decidir(0.99, Ctx(dispNuevo: true, simOClave: true, salida: true));
        Assert.Equal(NivelInterno.Retener, retiene.Nivel);
        Assert.Equal(PisoSeguridad.Ninguno, retiene.Piso);
        // Con p >= retener, fan-in y reputación del destino bajos: el modelo rechaza y el piso no lo baja.
        var rechaza = Politica.Decidir(0.99, Ctx(rep: 10, fanin: 5, dispNuevo: true, simOClave: true, salida: true, transf: true, destinoNuevo: true, sobreSaldo: 0.95));
        Assert.Equal(NivelInterno.Rechazar, rechaza.Nivel);
        // El modelo ya desafiaba: una regla que también desafiaría deja el nivel del modelo y no marca el piso.
        var verif = Politica.Decidir(0.8, Ctx(dispNuevo: true, simOClave: true, salida: true));
        Assert.Equal(NivelInterno.Verificar, verif.Nivel);
        Assert.Equal(PisoSeguridad.Ninguno, verif.Piso);
        // Pero un desafío del modelo SÍ se sube a retener si el piso exige retener.
        Assert.Equal(NivelInterno.Retener, Politica.Decidir(0.8, Ctx(true, true, true, true, true, 0.95)).Nivel);
        // Cuenta bloqueada: siempre rechazar
        Assert.Equal(NivelInterno.Rechazar, Politica.Decidir(0.0, Ctx(bloqueada: true, dispNuevo: true, simOClave: true, salida: true)).Nivel);
    }
}
