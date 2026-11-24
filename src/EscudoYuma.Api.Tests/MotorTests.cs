using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EscudoYuma.Api.Features.Auth.Login;
using EscudoYuma.Api.Features.Sandbox.Replay;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Reglas;
using EscudoYuma.Api.Shared.Scoring;
using Microsoft.Extensions.DependencyInjection;

namespace EscudoYuma.Api.Tests;

public class ExplicadorTests
{
    private static readonly ExplicadorRazones Explicador = new(RutasDatos.Artefacto("explicacion.json"));

    private static EstadoEnLinea NuevoEstado() => new(new ConstructorAtributos(new CatalogoReglas(CatalogoReglas.ReglasVigentes()), RutasDatos.Imputacion));

    [Theory]
    [InlineData("Monto igual al {v:.0%} del saldo", 0.96, "Monto igual al 96 % del saldo")]
    [InlineData("Monto alto (${v:N0} COP)", 1900000, "Monto alto ($1.900.000 COP)")]
    [InlineData("Monto {v:.1f} veces superior a su promedio", 4.27, "Monto 4.3 veces superior a su promedio")]
    [InlineData("El destino recibió de {v:.0f} remitentes distintos en 7 días", 4, "El destino recibió de 4 remitentes distintos en 7 días")]
    public void Las_plantillas_incluyen_valores_concretos(string plantilla, double v, string esperado) =>
        Assert.Equal(esperado, ExplicadorRazones.Formatear(plantilla, v));

    [Fact]
    public void La_contribucion_es_el_lookup_de_la_cubeta_y_un_vaciado_de_saldo_suma_riesgo()
    {
        var bajo = new double[CatalogoAtributos.N];
        var alto = new double[CatalogoAtributos.N];
        alto[CatalogoAtributos.Indice("monto_sobre_saldo")] = 0.96;
        alto[CatalogoAtributos.Indice("destino_nuevo")] = 1;
        Assert.True(Explicador.Contribuciones(alto)[CatalogoAtributos.Indice("monto_sobre_saldo")]
                    > Explicador.Contribuciones(bajo)[CatalogoAtributos.Indice("monto_sobre_saldo")]);
    }

    [Fact]
    public void Una_operacion_riesgosa_da_de_1_a_3_razones_y_menciona_valores_del_caso()
    {
        var estado = NuevoEstado();
        var cid = "C-EXP";
        foreach (var (tipo, h) in new[] { ("cambio_sim_detectado", "13:50:00"), ("cambio_clave", "13:55:00"), ("registro_dispositivo", "14:00:00") })
            estado.ProcesarEvento(new EventoApp("E" + tipo, $"2026-07-15 {h}", cid, tipo, "DISP-1", "Android", "Antioquia", "exitoso"));
        var tx = FabricaApi.Tx(cid, monto: 1_900_000, saldo: 2_000_000, riesgosa: true);
        var razones = Explicador.Explicar(estado.ProcesarTx(tx).Atributos);

        Assert.InRange(razones.Count, 1, 3);
        Assert.All(razones, r => Assert.DoesNotMatch(@"\{|\}", r));
        Assert.Contains(razones, r => r.Any(char.IsDigit)); // al menos una razón con un valor concreto (95 %, monto, minutos...)
    }

    [Fact]
    public void Si_ninguna_contribucion_supera_el_umbral_no_hay_razones_de_atributos_y_la_generica_es_coherente()
    {
        // Explicador con todas las tablas en cero: ningún atributo supera el umbral (0,15).
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(RutasDatos.Artefacto("explicacion.json")))!;
        var ceros = json["tablas"].EnumerateArray().Select(t => t.EnumerateArray().Select(_ => 0.0).ToArray()).ToArray();
        var ruta = Path.Combine(Path.GetTempPath(), "explicacion-ceros-" + Guid.NewGuid().ToString("N") + ".json");
        var doc = json.ToDictionary(kv => kv.Key, kv => kv.Key == "tablas" ? (object)ceros : kv.Value);
        File.WriteAllText(ruta, JsonSerializer.Serialize(doc));
        try
        {
            var sinContribucion = new ExplicadorRazones(ruta);
            Assert.Empty(sinContribucion.Explicar(NuevoEstado().ProcesarTx(FabricaApi.Tx("C-NORMAL-EXP")).Atributos));
        }
        finally { File.Delete(ruta); }
        Assert.Contains("Sin señales", ExplicadorRazones.RazonGenerica(NivelInterno.Aprobar, 0.001));
        Assert.Contains("retenida", ExplicadorRazones.RazonGenerica(NivelInterno.Retener, 0.98));
        Assert.Contains("rechazada", ExplicadorRazones.RazonGenerica(NivelInterno.Rechazar, 0.99));
        Assert.Contains("verificación", ExplicadorRazones.RazonGenerica(NivelInterno.Verificar, 0.6));
    }
}

public class ScoreIngresoLoginTests
{
    private static readonly ScoreIngresoLogin Score = new();
    private static ContextoLogin Ctx(bool tiene = true, bool conocido = true, int f1 = 0, int f24 = 0, bool sim = false, bool clave = false, bool reg = false,
        string? dep = "Antioquia", string? habitual = "Antioquia", int obs = 10, int hora = 14) =>
        new(tiene, conocido, f1, f24, sim, clave, reg, dep, habitual, obs, hora);

    [Fact]
    public void Sin_senales_el_score_es_cero()
    {
        var r = Score.Calcular(Ctx());
        Assert.Equal(0, r.Score);
        Assert.Single(r.Razones);
    }

    [Theory]
    [InlineData(0.30, true, 0, 0, false, false, false, "Antioquia", 14)]   // dispositivo no visto
    [InlineData(0.12, false, 1, 1, false, false, false, "Antioquia", 14)]   // 1 fallido en 1 h
    [InlineData(0.36, false, 3, 3, false, false, false, "Antioquia", 14)]   // 3 fallidos: tope 0,36
    [InlineData(0.36, false, 5, 5, false, false, false, "Antioquia", 14)]   // más de 3: sigue en el tope
    [InlineData(0.36 + 0.06, false, 3, 5, false, false, false, "Antioquia", 14)] // +2 en 24 h: 0,03 c/u
    [InlineData(0.36 + 0.09, false, 3, 9, false, false, false, "Antioquia", 14)] // tope de extras 0,09
    [InlineData(0.25, false, 0, 0, true, false, false, "Antioquia", 14)]    // cambio de SIM
    [InlineData(0.20, false, 0, 0, false, true, false, "Antioquia", 14)]    // cambio de clave
    [InlineData(0.10, false, 0, 0, false, false, true, "Antioquia", 14)]    // registro de dispositivo
    [InlineData(0.20, false, 0, 0, false, false, false, "Chocó", 14)]       // IP distinta a la habitual
    [InlineData(0.08, false, 0, 0, false, false, false, "Antioquia", 3)]    // madrugada (hora <= 4)
    [InlineData(0.00, false, 0, 0, false, false, false, "Antioquia", 5)]    // 05:00 ya no es madrugada
    public void Cada_senal_aporta_su_peso(double esperado, bool nuevoDisp, int f1, int f24, bool sim, bool clave, bool reg, string dep, int hora)
    {
        var r = Score.Calcular(Ctx(conocido: !nuevoDisp, f1: f1, f24: f24, sim: sim, clave: clave, reg: reg, dep: dep, hora: hora));
        Assert.Equal(esperado, r.Score, 10);
    }

    [Fact]
    public void La_ip_distinta_exige_al_menos_3_observaciones_y_el_dispositivo_no_visto_exige_dispositivos_conocidos()
    {
        Assert.Equal(0, Score.Calcular(Ctx(dep: "Chocó", obs: 2)).Score);
        Assert.Equal(0.20, Score.Calcular(Ctx(dep: "Chocó", obs: 3)).Score, 10);
        Assert.Equal(0, Score.Calcular(Ctx(tiene: false, conocido: false)).Score); // cliente sin dispositivos: no hay "no visto"
    }

    [Fact]
    public void El_score_se_acota_a_1_y_cada_senal_deja_su_razon()
    {
        var r = Score.Calcular(Ctx(conocido: false, f1: 5, f24: 9, sim: true, clave: true, reg: true, dep: "Chocó", hora: 2));
        Assert.Equal(1.0, r.Score); // 0,30 + 0,36 + 0,09 + 0,25 + 0,20 + 0,10 + 0,20 + 0,08 > 1
        Assert.Equal(8, r.Razones.Count);
    }

    [Fact]
    public void Los_umbrales_de_login_documentados_son_040_y_070()
    {
        var o = new LoginOptions();
        Assert.Equal(0.40, o.UmbralOtp);
        Assert.Equal(0.70, o.UmbralBloqueo);
    }
}

public class EstadoEnLineaTests
{
    private static EstadoEnLinea NuevoEstado() => new(new ConstructorAtributos(new CatalogoReglas(CatalogoReglas.ReglasVigentes()), RutasDatos.Imputacion));

    [Fact]
    public void No_hay_fuga_agregar_transacciones_y_eventos_posteriores_no_cambia_los_atributos_previos()
    {
        var txs = Enumerable.Range(0, 40).Select(i => FabricaApi.Tx($"C{i % 4}", monto: 10_000 + i * 1_000, hora: $"{8 + i / 4:00}:{i % 4 * 10:00}:00", id: $"T{i:000}",
            idDestino: $"EXT{i % 3}")).ToList();
        var eventos = Enumerable.Range(0, 30).Select(i => new EventoApp($"E{i:000}", $"2026-07-15 {8 + i / 3:00}:{i % 3 * 15 + 5:00}:00", $"C{i % 4}",
            i % 2 == 0 ? TiposEvento.InicioSesion : TiposEvento.CambioSim, "D1", "Android", "Antioquia", i % 3 == 0 ? "fallido" : "exitoso")).ToList();
        const int corte = 20;
        var tCorte = txs[corte - 1].Ts;

        var parcial = Correr(txs.Take(corte).ToList(), eventos.Where(e => e.Ts <= tCorte).ToList());
        var completo = Correr(txs, eventos).Take(corte).ToList();
        for (var i = 0; i < corte; i++) Assert.Equal(parcial[i], completo[i]);
    }

    /// <summary>Mismo flujo cronologico que features.py: a igual segundo la transaccion va antes que los eventos.</summary>
    private static List<double[]> Correr(List<Transaccion> txs, List<EventoApp> eventos)
    {
        var estado = NuevoEstado();
        var ie = 0;
        var salida = new List<double[]>();
        foreach (var tx in txs)
        {
            while (ie < eventos.Count && eventos[ie].Ts < tx.Ts) estado.ProcesarEvento(eventos[ie++]);
            salida.Add(estado.ProcesarTx(tx).Atributos.Valores);
        }
        return salida;
    }

    [Fact]
    public void Es_thread_safe_con_escrituras_concurrentes_de_muchos_clientes()
    {
        var estado = NuevoEstado();
        const int hilos = 8, porHilo = 400;
        Parallel.For(0, hilos, h =>
        {
            for (var i = 0; i < porHilo; i++)
            {
                estado.ProcesarEvento(new EventoApp($"E{h}-{i}", "2026-07-15 10:00:00", $"C{h}", TiposEvento.InicioSesion, "D1", "Android", "Antioquia", i % 5 == 0 ? "fallido" : "exitoso"));
                var r = estado.ProcesarTx(FabricaApi.Tx($"C{h}", id: $"T{h}-{i}", idDestino: $"EXT{i % 7}"));
                Assert.Equal(CatalogoAtributos.N, r.Atributos.Valores.Length);
            }
        });
        // cada cliente procesó exactamente sus 400 transacciones
        for (var h = 0; h < hilos; h++)
            Assert.Equal(porHilo, estado.ProcesarTx(FabricaApi.Tx($"C{h}", id: $"TF{h}")).Atributos["n_tx_previas"]);
    }

    [Fact]
    public void La_ip_habitual_es_la_moda_con_desempate_por_nombre_y_el_registro_no_cuenta_como_visto()
    {
        var e = NuevoEstado();
        void Login(string id, string dep, string disp = "D1") => e.ProcesarEvento(new EventoApp(id, "2026-07-15 09:00:00", "CX", TiposEvento.InicioSesion, disp, "Android", dep, "exitoso"));
        Login("1", "Valle"); Login("2", "Antioquia"); Login("3", "Valle"); Login("4", "Antioquia");
        var ctx = e.ConsultarLogin("CX", Tiempo.AEpoch("2026-07-15 10:00:00"), "D1", "Valle");
        Assert.Equal("Antioquia", ctx.IpHabitual); // empate 2-2: gana el nombre menor
        Assert.Equal(4, ctx.ObservacionesIp);

        e.ProcesarEvento(new EventoApp("5", "2026-07-15 09:30:00", "CX", TiposEvento.RegistroDispositivo, "D2", "Android", null, "exitoso"));
        var tras = e.ConsultarLogin("CX", Tiempo.AEpoch("2026-07-15 10:00:00"), "D2", "Valle");
        Assert.False(tras.DispositivoConocido);
        Assert.True(tras.Registro24h);
    }
}

public class EventosEndpointTests(FabricaApi api) : IClassFixture<FabricaApi>
{
    private static object Lote(params object[] eventos) => new { eventos };
    private static object Evento(string id = "E1", string cliente = "C-EV", string tipo = "cambio_sim_detectado", string fecha = "2026-07-15 09:00:00", string resultado = "exitoso") =>
        new { id_evento = id, fecha_hora = fecha, id_cliente = cliente, tipo_evento = tipo, id_dispositivo = "D1", sistema_operativo = "Android", departamento_ip = "Antioquia", resultado };

    [Fact]
    public async Task Solo_el_rol_servicio_puede_ingerir_eventos()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsJsonAsync("/v1/eventos", Lote(Evento()))).StatusCode);
        var cliente = await api.ClienteAutenticadoAsync("C-EV-CLI", Roles.Cliente);
        var analista = await api.ClienteAutenticadoAsync("ANA-EV", Roles.Analista);
        Assert.Equal(HttpStatusCode.Forbidden, (await cliente.PostAsJsonAsync("/v1/eventos", Lote(Evento()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analista.PostAsJsonAsync("/v1/eventos", Lote(Evento()))).StatusCode);
    }

    [Fact]
    public async Task Ingerir_eventos_actualiza_el_estado_en_linea_y_los_atributos_de_la_siguiente_transaccion()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-EV", Roles.Servicio);
        var resp = await servicio.PostAsJsonAsync("/v1/eventos", Lote(
            Evento("E-A", "C-EV-ATR", "cambio_sim_detectado", "2026-07-15 13:00:00"),
            Evento("E-B", "C-EV-ATR", "inicio_sesion", "2026-07-15 13:10:00", "fallido"),
            Evento("E-C", "C-EV-ATR", "inicio_sesion", "2026-07-15 13:20:00", "fallido")));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("procesados").GetInt32());

        var estado = api.Services.GetRequiredService<IEstadoEnLinea>();
        var tx = FabricaApi.Tx("C-EV-ATR", hora: "14:05:00");
        var a = estado.ProcesarTx(tx).Atributos;
        Assert.Equal(2, a["login_fallidos_1h"]);
        Assert.Equal(2, a["login_fallidos_24h"]);
        Assert.Equal(65, a["min_desde_cambio_sim"]); // 13:00 -> 14:05
        Assert.Equal(0, a["dispositivo_visto_antes"]); // el login fallido no cuenta como visto
    }

    [Theory]
    [InlineData("tipo_inventado", "2026-07-15 09:00:00", "exitoso")]
    [InlineData("inicio_sesion", "15/07/2026", "exitoso")]
    [InlineData("inicio_sesion", "2026-07-15 09:00:00", "quizas")]
    public async Task Eventos_invalidos_devuelven_400(string tipo, string fecha, string resultado)
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-EV2", Roles.Servicio);
        Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsJsonAsync("/v1/eventos", Lote(Evento(tipo: tipo, fecha: fecha, resultado: resultado)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsJsonAsync("/v1/eventos", Lote())).StatusCode); // lote vacío
    }
}

public class MetricasLocalesTests
{
    private static TxCalificada C(string id, string fecha, string decision) => new(id, fecha, decision, 0.5);

    [Fact]
    public void Calcula_las_metricas_de_evaluar_py_solo_en_sep_oct_y_cruza_con_la_etiqueta()
    {
        var etiquetas = new Dictionary<string, EtiquetaTx>
        {
            ["F1"] = new(true, "sim_swapping", 100), ["F2"] = new(true, "sim_swapping", 300), ["F3"] = new(true, "cuenta_mula", 100),
            ["F4"] = new(true, "engano_qr", 500),
            ["L1"] = new(false, "", 10), ["L2"] = new(false, "", 10), ["L3"] = new(false, "", 10), ["L4"] = new(false, "", 10),
            ["FUERA"] = new(true, "sim_swapping", 999), ["FUERA2"] = new(true, "sim_swapping", 999), // antes de septiembre / después de octubre
        };
        var tx = new[]
        {
            C("F1", "2026-09-02 10:00:00", "retener"), C("F2", "2026-09-10 10:00:00", "desafiar"), C("F3", "2026-10-31 23:59:59", "rechazar"),
            C("F4", "2026-10-05 10:00:00", "aprobar"),
            C("L1", "2026-09-03 10:00:00", "aprobar"), C("L2", "2026-09-04 10:00:00", "desafiar"), C("L3", "2026-09-05 10:00:00", "retener"), C("L4", "2026-10-01 10:00:00", "aprobar"),
            C("FUERA", "2026-08-31 23:59:59", "aprobar"), C("FUERA2", "2026-11-01 00:00:00", "aprobar"),
        };
        var m = MetricasLocales.Calcular(tx, etiquetas, "2026-09-01", "2026-10-31");
        Assert.Equal(8, m["n_tx"]);
        Assert.Equal(4, m["n_fraudes"]);
        Assert.Equal(75.0, (double)m["deteccion_casos_pct"]!, 9);                 // 3 de 4 fraudes con desafiar/retener/rechazar
        Assert.Equal(100.0 * 500 / 1000, 100.0 - (double)m["deteccion_monto_pct"]!, 9); // el fraude de 500 no se detectó
        Assert.Equal(50.0, (double)m["fraude_retenido_o_rechazado_pct"]!, 9);     // F1 y F3
        Assert.Equal(100.0 * 1 / (2 + 1), (double)m["fp_pct"]!, 9);               // 1 FP entre 2 fraudes bloqueados + 1 FP
        Assert.Equal(100.0 * 1 / (3 + 1), (double)m["fp_pct_sandbox"]!, 9);       // el sandbox cuenta los desafiados como detectados
        Assert.Equal(25.0, (double)m["legitimas_desafiadas_pct"]!, 9);
        Assert.Equal(25.0, (double)m["legitimas_retenidas_pct"]!, 9);
        var mod = (Dictionary<string, object?>)m["deteccion_por_modalidad_pct"]!;
        Assert.Equal(100.0, (double)mod["sim_swapping"]!, 9);
        Assert.Equal(0.0, (double)mod["engano_qr"]!, 9);
    }

    [Fact]
    public void El_percentil_usa_rango_mas_cercano_como_el_sandbox()
    {
        var v = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        Assert.Equal(51, MetricasLocales.Percentil(v, 0.50));
        Assert.Equal(100, MetricasLocales.Percentil(v, 0.99));
        Assert.Equal(0, MetricasLocales.Percentil([], 0.99));
    }
}
