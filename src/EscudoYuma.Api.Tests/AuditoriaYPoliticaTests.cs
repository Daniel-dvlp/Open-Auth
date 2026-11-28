using System.Net;
using System.Net.Http.Json;
using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Scoring;
using EscudoYuma.Api.Shared.Seguridad;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EscudoYuma.Api.Tests;

public class PoliticaDecisionTests
{
    [Theory]
    [InlineData(NivelInterno.Aprobar, "aprobar")]
    [InlineData(NivelInterno.Advertir, "desafiar")]
    [InlineData(NivelInterno.Verificar, "desafiar")]
    [InlineData(NivelInterno.Retener, "retener")]
    [InlineData(NivelInterno.Rechazar, "rechazar")]
    public void Mapea_5_niveles_internos_a_4_decisiones(NivelInterno nivel, string esperada) =>
        Assert.Equal(esperada, PoliticaDecision.Mapear(nivel));

    private static readonly PoliticaDecision Politica = new(UmbralesDecision.Cargar(RutasDatos.Artefacto("umbrales.json")));
    private static ContextoPolitica Ctx(double fanin = 0, double rep = 100, bool destinoNuevo = false, double sobreSaldo = 0.1,
        bool senales = false, double? ingreso = null, bool bloqueada = false) => new(fanin, rep, destinoNuevo, sobreSaldo, senales, ingreso, bloqueada);

    [Fact]
    public void Los_umbrales_vienen_de_umbrales_json()
    {
        Assert.Equal(0.5657939314842224, Politica.Umbrales.Desafiar);
        Assert.Equal(0.9709348082542419, Politica.Umbrales.Retener);
        Assert.Equal(3, Politica.Umbrales.RechazoFanInMin);
        Assert.Equal(30, Politica.Umbrales.RechazoReputacionMax);
    }

    [Theory]
    [InlineData(0.00, NivelInterno.Aprobar)]
    [InlineData(0.56, NivelInterno.Aprobar)]
    [InlineData(0.5657939314842224, NivelInterno.Verificar)] // p >= desafiar (borde inclusivo)
    [InlineData(0.5657934, NivelInterno.Verificar)]         // ONNX difiere de XGBoost ~1e-6 en el caso borde: la holgura 1e-6 lo conserva
    [InlineData(0.56579, NivelInterno.Aprobar)]                // pero 4e-6 por debajo ya es aprobar
    [InlineData(0.90, NivelInterno.Verificar)]
    [InlineData(0.9709348082542419, NivelInterno.Retener)]    // p >= retener (borde inclusivo)
    [InlineData(1.00, NivelInterno.Retener)]
    public void Los_umbrales_definen_el_nivel(double p, NivelInterno esperado) =>
        Assert.Equal(esperado, Politica.Decidir(p, Ctx()).Nivel);

    [Fact]
    public void Rechaza_solo_si_retener_y_fanin_y_reputacion_destino_lo_exigen()
    {
        Assert.Equal(NivelInterno.Rechazar, Politica.Decidir(0.99, Ctx(fanin: 3, rep: 30)).Nivel);
        Assert.Equal(NivelInterno.Retener, Politica.Decidir(0.99, Ctx(fanin: 2, rep: 30)).Nivel);   // fan-in insuficiente
        Assert.Equal(NivelInterno.Retener, Politica.Decidir(0.99, Ctx(fanin: 3, rep: 31)).Nivel);   // reputación no tan baja
        Assert.Equal(NivelInterno.Verificar, Politica.Decidir(0.80, Ctx(fanin: 5, rep: 0)).Nivel);  // sin p >= retener no se rechaza
    }

    [Fact]
    public void Desafiar_se_parte_en_advertir_y_verificar_segun_las_senales()
    {
        // destino nuevo + monto casi igual al saldo, sin señales de cuenta -> anti-ingeniería social
        Assert.Equal(NivelInterno.Advertir, Politica.Decidir(0.7, Ctx(destinoNuevo: true, sobreSaldo: 0.96)).Nivel);
        // con señales de dispositivo/SIM/clave/login -> verificar (toma de cuenta)
        Assert.Equal(NivelInterno.Verificar, Politica.Decidir(0.7, Ctx(destinoNuevo: true, sobreSaldo: 0.96, senales: true)).Nivel);
        // destino conocido -> verificar
        Assert.Equal(NivelInterno.Verificar, Politica.Decidir(0.7, Ctx(destinoNuevo: false, sobreSaldo: 0.96)).Nivel);
    }

    [Fact]
    public void Cuenta_bloqueada_por_analista_se_rechaza_siempre()
    {
        Assert.Equal(NivelInterno.Rechazar, Politica.Decidir(0.0, Ctx(bloqueada: true)).Nivel);
    }

    [Theory]
    [InlineData(0.39, 0.30, NivelInterno.Aprobar, false)]   // ingreso poco riesgoso: no escala
    [InlineData(0.40, 0.19, NivelInterno.Aprobar, false)]   // p < 0,2: no escala
    [InlineData(0.40, 0.20, NivelInterno.Verificar, true)]  // ingreso >= 0,4 y p >= 0,2: sube a verificar
    [InlineData(0.85, 0.35, NivelInterno.Verificar, true)]
    public void El_score_de_ingreso_solo_escala_aprobar_a_verificar(double ingreso, double p, NivelInterno esperado, bool escalada)
    {
        var r = Politica.Decidir(p, Ctx(ingreso: ingreso));
        Assert.Equal(esperado, r.Nivel);
        Assert.Equal(escalada, r.EscaladaPorIngreso);
    }

    [Fact]
    public void El_score_de_ingreso_nunca_baja_una_decision()
    {
        Assert.Equal(NivelInterno.Retener, Politica.Decidir(0.99, Ctx(ingreso: 0.0)).Nivel);
        Assert.Equal(NivelInterno.Verificar, Politica.Decidir(0.6, Ctx(ingreso: 0.0, senales: true)).Nivel);
    }

    [Fact]
    public void Todos_los_niveles_tienen_mapeo_a_una_decision_valida()
    {
        var validas = new[] { "aprobar", "desafiar", "retener", "rechazar" };
        foreach (var n in Enum.GetValues<NivelInterno>()) Assert.Contains(PoliticaDecision.Mapear(n), validas);
    }
}

public class AuditoriaTests(FabricaApi api) : IClassFixture<FabricaApi>
{
    private static RegistroAuditoria Reg(long seq, string previo, string detalle = "{}")
    {
        var r = new RegistroAuditoria
        {
            Secuencia = seq, FechaUtc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc).AddSeconds(seq), Evento = "evento", Actor = "actor",
            IdCorrelacion = "corr", Recurso = "recurso", Detalle = detalle, HashPrevio = previo,
        };
        r.Hash = CadenaHash.Calcular(r);
        return r;
    }

    private static List<RegistroAuditoria> Cadena(int n)
    {
        var l = new List<RegistroAuditoria>();
        var previo = CadenaHash.Genesis;
        for (var i = 1; i <= n; i++) { var r = Reg(i, previo, $"{{\"n\":{i}}}"); l.Add(r); previo = r.Hash; }
        return l;
    }

    [Fact]
    public void Una_cadena_integra_es_valida() => Assert.True(CadenaHash.Verificar(Cadena(5)).Valida);

    [Fact]
    public void Detecta_un_registro_modificado()
    {
        var c = Cadena(5);
        c[2].Detalle = "{\"n\":999}"; // manipulación del contenido
        var res = CadenaHash.Verificar(c);
        Assert.False(res.Valida);
        Assert.Equal(3, res.PrimeraSecuenciaRota);
    }

    [Fact]
    public void Detecta_un_registro_eliminado_o_reordenado_y_el_hash_recalculado_ajeno()
    {
        var c = Cadena(5);
        var sinUno = c.Where(r => r.Secuencia != 3).ToList();
        Assert.False(CadenaHash.Verificar(sinUno).Valida);

        var recalculado = Cadena(5);
        recalculado[1].Detalle = "{\"n\":42}";
        recalculado[1].Hash = CadenaHash.Calcular(recalculado[1]); // el atacante recalcula su propio hash...
        Assert.False(CadenaHash.Verificar(recalculado).Valida);   // ...pero rompe el enlace con el siguiente registro
    }

    [Fact]
    public async Task La_cadena_escrita_por_la_API_es_valida_y_la_tabla_es_append_only()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-AUD", Roles.Servicio);
        for (var i = 0; i < 3; i++)
            await servicio.PostAsJsonAsync("/v1/score/transaccion", FabricaApi.Tx("C-AUD"), FabricaApi.Snake);

        using var scope = api.Services.CreateScope();
        var aud = scope.ServiceProvider.GetRequiredService<ServicioAuditoria>();
        var res = await aud.VerificarAsync();
        Assert.True(res.Valida);
        Assert.True(res.Total >= 4); // login + 3 evaluaciones

        // La aplicación no puede modificar ni borrar registros
        var db = scope.ServiceProvider.GetRequiredService<EscudoDbContext>();
        var primero = await db.Auditoria.OrderBy(r => r.Secuencia).FirstAsync();
        primero.Detalle = "{\"manipulado\":true}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Escrituras_concurrentes_se_agrupan_en_commits_y_la_cadena_sigue_valida_y_sin_huecos()
    {
        const int n = 300;
        await Task.WhenAll(Enumerable.Range(0, n).Select(async i =>
        {
            using var s = api.Services.CreateScope();
            await s.ServiceProvider.GetRequiredService<ServicioAuditoria>().RegistrarAsync("prueba_concurrente", "pruebas", new { i }, actor: "test");
        }));

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EscudoDbContext>();
        // Al volver cada RegistrarAsync su registro ya está confirmado: ninguno se perdió ni se duplicó.
        Assert.Equal(n, await db.Auditoria.CountAsync(r => r.Evento == "prueba_concurrente"));
        var res = await scope.ServiceProvider.GetRequiredService<ServicioAuditoria>().VerificarAsync();
        Assert.True(res.Valida, $"cadena rota en la secuencia {res.PrimeraSecuenciaRota}");
    }

    [Fact]
    public async Task El_detalle_de_auditoria_no_contiene_la_llave_completa()
    {
        var llave = await api.SembrarAsync("C-PII", llave: "3001234567");
        await api.LoginAsync(llave, "mala");
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EscudoDbContext>();
        var detalles = await db.Auditoria.Select(r => r.Detalle).ToListAsync();
        Assert.DoesNotContain(detalles, d => d.Contains("3001234567"));
        Assert.Contains(detalles, d => d.Contains("300****567"));
    }
}

/// <summary>Garantía de negocio: solo ResolverAlerta (analista) puede dejar una cuenta con bloqueo definitivo.</summary>
public class BloqueoDefinitivoTests(FabricaApi api) : IClassFixture<FabricaApi>
{
    private async Task<int> BloqueadasAsync()
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EscudoDbContext>().Reputaciones.CountAsync(r => r.BloqueoDefinitivo);
    }

    [Fact]
    public async Task Ninguna_ruta_distinta_de_ResolverAlerta_bloquea_una_cuenta()
    {
        // 1) Rutas realmente mapeadas en la aplicación (excluye infraestructura).
        var rutas = api.Services.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Select(e => (Metodo: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET", Ruta: e.RoutePattern.RawText!))
            .Where(r => r.Ruta is not ("/metrics" or "/health/live" or "/health/ready"))
            .Select(r => $"{r.Metodo} {r.Ruta}").Distinct().OrderBy(x => x).ToList();

        // 2) Todas las rutas conocidas se ejercitan abajo; si alguien agrega una ruta nueva, este test obliga a incluirla.
        var ejercitadas = new SortedSet<string>();
        var analista = await api.ClienteAutenticadoAsync("ANA-BLK", Roles.Analista);
        var servicio = await api.ClienteAutenticadoAsync("SVC-BLK", Roles.Servicio);
        var anonimo = api.CreateClient();
        await api.SembrarAsync("C-BLK");

        async Task Llamar(HttpClient c, string metodo, string ruta, string rutaReal, object? cuerpo = null, bool snake = false)
        {
            ejercitadas.Add($"{metodo} {ruta}");
            var req = new HttpRequestMessage(new HttpMethod(metodo), rutaReal);
            if (cuerpo is not null) req.Content = JsonContent.Create(cuerpo, options: snake ? FabricaApi.Snake : null);
            using var resp = await c.SendAsync(req);
            Assert.True((int)resp.StatusCode < 500, $"{metodo} {ruta} respondió {(int)resp.StatusCode}");
        }

        await Llamar(anonimo, "GET", "/v1/salud", "/v1/salud");
        await Llamar(anonimo, "POST", "/v1/auth/login", "/v1/auth/login",
            new { llave = "k-c-blk", clave = FabricaApi.Clave, id_dispositivo = "DISP-1", departamento_ip = FabricaApi.Depto });
        await Llamar(analista, "POST", "/v1/auth/dispositivos", "/v1/auth/dispositivos", new { id_dispositivo = "DISP-NUEVO-BLK", sistema_operativo = "iOS" });
        // Varias evaluaciones: retener, rechazar y aprobar (mucho riesgo, y aun así nadie bloquea la cuenta).
        await Llamar(servicio, "POST", "/v1/score/transaccion", "/v1/score/transaccion", FabricaApi.TxRetener("C-BLK"), snake: true);
        await Llamar(servicio, "POST", "/v1/score/transaccion", "/v1/score/transaccion",
            FabricaApi.Tx("C-BLK", monto: 1_900_000, saldo: 2_000_000, riesgosa: true, hora: "02:10:00"), snake: true);
        await Llamar(servicio, "POST", "/v1/score/transaccion", "/v1/score/transaccion", FabricaApi.Tx("C-BLK"), snake: true);
        await Llamar(servicio, "POST", "/v1/eventos", "/v1/eventos", new
        {
            eventos = new[] { new { id_evento = "E-BLK", fecha_hora = "2026-07-15 13:00:00", id_cliente = "C-BLK", tipo_evento = "cambio_sim_detectado", resultado = "exitoso" } },
        });
        await Llamar(analista, "GET", "/v1/sandbox/replay", "/v1/sandbox/replay");
        await Llamar(analista, "POST", "/v1/sandbox/replay", "/v1/sandbox/replay"); // 409: replay deshabilitado por defecto
        await Llamar(analista, "GET", "/v1/alertas", "/v1/alertas");
        await Llamar(analista, "GET", "/v1/cuentas/{idCuenta}/reputacion", "/v1/cuentas/C-BLK/reputacion");
        // ResolverAlerta con falso positivo / sin acción de bloqueo tampoco bloquea
        await Llamar(analista, "POST", "/v1/alertas/{idAlerta:guid}/resolver", $"/v1/alertas/{Guid.NewGuid()}/resolver",
            new { resolucion = "falso_positivo", accion = "liberar_transaccion" });

        // Rutas de integración con la billetera (rol servicio): ninguna bloquea cuentas.
        await Llamar(servicio, "POST", "/v1/auth/evaluar-ingreso", "/v1/auth/evaluar-ingreso",
            new { id_cliente = "C-BLK", id_dispositivo = "DISP-OTRO", departamento_ip = "Bogotá D.C.", dispositivos_conocidos = new[] { "DISP-1" } });
        await Llamar(servicio, "POST", "/v1/alertas/desafio-fallido", "/v1/alertas/desafio-fallido",
            new { id_transaccion = "TY-BLK-1", id_cliente = "C-BLK", monto_cop = 1_900_000, metodo = "biometria", detalle = "no coincide", puntaje = 0.9 });
        await Llamar(servicio, "GET", "/v1/transacciones/{idTransaccion}/decision", "/v1/transacciones/TY-BLK-1/decision");
        // Expediente y narrativas (analista, solo lectura o encolar): tampoco bloquean.
        await Llamar(analista, "GET", "/v1/alertas/{idAlerta:guid}/expediente", $"/v1/alertas/{Guid.NewGuid()}/expediente");
        await Llamar(analista, "GET", "/v1/alertas/{idAlerta:guid}/narrativa", $"/v1/alertas/{Guid.NewGuid()}/narrativa");
        await Llamar(analista, "POST", "/v1/alertas/{idAlerta:guid}/narrativa/regenerar", $"/v1/alertas/{Guid.NewGuid()}/narrativa/regenerar");
        await Llamar(analista, "GET", "/v1/clientes/{idCliente}/resumen", "/v1/clientes/C-BLK/resumen");

        Assert.Equal(rutas, ejercitadas.ToList());
        Assert.Equal(0, await BloqueadasAsync());
    }

    [Fact]
    public async Task Solo_ResolverAlerta_con_fraude_confirmado_y_accion_bloquear_marca_el_bloqueo()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-BLK2", Roles.Servicio);
        var analista = await api.ClienteAutenticadoAsync("ANA-BLK2", Roles.Analista);
        await FabricaApi.PrepararRiesgoAsync(servicio, "C-BLK2");
        var tx = FabricaApi.TxRetener("C-BLK2");
        await servicio.PostAsJsonAsync("/v1/score/transaccion", tx, FabricaApi.Snake);

        var antes = await BloqueadasAsync();
        using var scope = api.Services.CreateScope();
        var alerta = await scope.ServiceProvider.GetRequiredService<EscudoDbContext>().Alertas.FirstAsync(a => a.IdTransaccion == tx.IdTransaccion);
        var resp = await analista.PostAsJsonAsync($"/v1/alertas/{alerta.Id}/resolver", new { resolucion = "fraude_confirmado", accion = "bloquear_cuenta" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(antes + 1, await BloqueadasAsync());
    }
}
