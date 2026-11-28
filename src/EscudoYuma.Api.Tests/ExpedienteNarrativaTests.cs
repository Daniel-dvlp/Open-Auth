using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EscudoYuma.Api.Shared.Expediente;
using EscudoYuma.Api.Shared.Persistencia;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EscudoYuma.Api.Tests;

/// <summary>Fase 4: expediente ordenado y enlazado a la auditoría; narrativa validada (LLM) con respaldo por plantilla.</summary>
public class ExpedienteNarrativaTests
{
    private static async Task<T> Esperar<T>(Func<Task<T?>> f, int ms = 8000) where T : class
    {
        var fin = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < fin)
        {
            if (await f() is { } r) return r;
            await Task.Delay(100);
        }
        throw new TimeoutException("No llegó a tiempo.");
    }

    private static async Task<JsonElement> EsperarJson(Func<Task<JsonElement?>> f, int ms = 8000)
    {
        var fin = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < fin)
        {
            if (await f() is { } r) return r;
            await Task.Delay(100);
        }
        throw new TimeoutException("No llegó a tiempo.");
    }

    private static async Task<(HttpClient Analista, Guid IdAlerta, string IdTx)> AlertaRealAsync(FabricaApi api, string cliente)
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-" + cliente, Roles.Servicio);
        var analista = await api.ClienteAutenticadoAsync("ANA-" + cliente, Roles.Analista);
        await FabricaApi.PrepararRiesgoAsync(servicio, cliente);
        var tx = FabricaApi.TxRetener(cliente);
        (await servicio.PostAsJsonAsync("/v1/score/transaccion", tx, FabricaApi.Snake)).EnsureSuccessStatusCode();
        var cola = await analista.GetFromJsonAsync<JsonElement>("/v1/alertas");
        var id = cola.GetProperty("alertas").EnumerateArray().Single(a => a.GetProperty("id_transaccion").GetString() == tx.IdTransaccion).GetProperty("id").GetGuid();
        return (analista, id, tx.IdTransaccion);
    }

    [Fact]
    public async Task Expediente_ordena_operacion_decision_eventos_y_normas_con_su_eslabon_de_auditoria()
    {
        using var api = new FabricaApi();
        var (analista, id, idTx) = await AlertaRealAsync(api, "C-EXP1");
        var e = await EsperarJson(async () =>
        {
            var j = await analista.GetFromJsonAsync<JsonElement>($"/v1/alertas/{id}/expediente");
            return j.GetProperty("hechos").GetArrayLength() >= 6 ? (JsonElement?)j : null; // 3 eventos + transacción + decisión + alerta
        });
        var hechos = e.GetProperty("hechos").EnumerateArray().ToList();
        var tipos = hechos.Select(h => h.GetProperty("tipo").GetString()).ToList();
        Assert.Equal(["evento_app", "evento_app", "evento_app"], tipos.Take(3)); // eventos 13:50-14:00, antes de la operación (14:05)
        Assert.Contains("transaccion", tipos);
        Assert.Contains("alerta", tipos);
        var decision = hechos.Single(h => h.GetProperty("tipo").GetString() == "decision");
        Assert.Equal("retener", decision.GetProperty("datos").GetProperty("decision").GetString());
        var normas = decision.GetProperty("normas").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("POL-UMBRALES", normas);
        Assert.Contains(normas, n => n!.StartsWith('R'));
        Assert.True(decision.GetProperty("secuencia_auditoria").GetInt64() > 0);
        Assert.Contains(e.GetProperty("normas_aplicadas").EnumerateArray(), n => n.GetProperty("codigo").GetString() == "POL-UMBRALES");
        Assert.Equal(idTx, e.GetProperty("id_transaccion").GetString());
        var fechas = hechos.Select(h => h.GetProperty("fecha_utc").GetDateTime()).ToList();
        Assert.Equal(fechas.OrderBy(f => f), fechas);
    }

    [Fact]
    public async Task Sin_Ollama_la_narrativa_sale_por_plantilla_y_queda_desactualizada_tras_la_resolucion()
    {
        using var api = new FabricaApi();
        var (analista, id, _) = await AlertaRealAsync(api, "C-EXP2");
        var n = await EsperarJson(async () =>
        {
            var r = await analista.GetAsync($"/v1/alertas/{id}/narrativa");
            return r.StatusCode == HttpStatusCode.OK ? (JsonElement?)await r.Content.ReadFromJsonAsync<JsonElement>() : null;
        });
        Assert.Equal("plantilla", n.GetProperty("origen").GetString());
        Assert.Contains("retener", n.GetProperty("contenido").GetProperty("resumen").GetString());
        Assert.False(n.GetProperty("desactualizada").GetBoolean());

        await analista.PostAsJsonAsync($"/v1/alertas/{id}/resolver", new { resolucion = "falso_positivo", accion = "liberar_transaccion" });
        var n2 = await EsperarJson(async () =>
        {
            var j = await analista.GetFromJsonAsync<JsonElement>($"/v1/alertas/{id}/narrativa");
            return j.GetProperty("desactualizada").GetBoolean() ? (JsonElement?)j : null;
        });
        Assert.True(n2.GetProperty("desactualizada").GetBoolean());
        Assert.Equal(HttpStatusCode.Accepted, (await analista.PostAsync($"/v1/alertas/{id}/narrativa/regenerar", null)).StatusCode);
    }

    [Fact]
    public async Task Narrativa_del_LLM_valida_se_guarda_y_una_con_cifra_inventada_se_descarta()
    {
        var falso = new NarradorFalso();
        using var api = new FabricaConNarrador(falso);
        var (analista, id, idTx) = await AlertaRealAsync(api, "C-EXP3");
        var n = await EsperarJson(async () =>
        {
            var r = await analista.GetAsync($"/v1/alertas/{id}/narrativa");
            return r.StatusCode == HttpStatusCode.OK ? (JsonElement?)await r.Content.ReadFromJsonAsync<JsonElement>() : null;
        });
        Assert.Equal("ollama", n.GetProperty("origen").GetString());
        Assert.Contains("900.000", n.GetProperty("contenido").GetProperty("resumen").GetString()); // cifra real del expediente

        falso.Inventar = true; // ahora el "LLM" cita un monto que no existe
        await analista.PostAsync($"/v1/alertas/{id}/narrativa/regenerar", null);
        var n2 = await EsperarJson(async () =>
        {
            var j = await analista.GetFromJsonAsync<JsonElement>($"/v1/alertas/{id}/narrativa");
            return j.GetProperty("origen").GetString() == "plantilla" ? (JsonElement?)j : null;
        });
        Assert.Contains("cifras", n2.GetProperty("motivo_respaldo").GetString());
        _ = idTx;
    }

    [Fact]
    public async Task Resumen_de_actividad_del_cliente_y_rutas_solo_para_analista()
    {
        using var api = new FabricaApi();
        var (analista, id, _) = await AlertaRealAsync(api, "C-EXP4");
        var servicio = await api.ClienteAutenticadoAsync("SVC-EXP4B", Roles.Servicio);
        Assert.Equal(HttpStatusCode.Forbidden, (await servicio.GetAsync($"/v1/alertas/{id}/expediente")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await servicio.GetAsync("/v1/clientes/C-EXP4/resumen")).StatusCode);
        var r = await EsperarJson(async () =>
        {
            var j = await analista.GetFromJsonAsync<JsonElement>("/v1/clientes/C-EXP4/resumen?dias=90");
            return j.GetProperty("hechos").GetInt32() >= 1 ? (JsonElement?)j : null; // los eventos del fixture son de julio; el caso es de hoy
        });
        Assert.Equal("plantilla", r.GetProperty("origen").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await analista.GetAsync("/v1/clientes/C-EXP4/resumen?dias=200")).StatusCode);
    }

    [Fact]
    public async Task Verificador_detecta_alteracion_de_la_cadena_y_la_registra()
    {
        using var api = new FabricaApi();
        await api.SembrarAsync("C-EXP5"); // genera registros de auditoría al crear usuarios? se asegura con un login
        await api.LoginAsync("k-C-EXP5");
        var verificador = api.Services.GetRequiredService<VerificadorCadena>();
        Assert.True((await verificador.VerificarAsync(default)).Valida);
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EscudoDbContext>();
            db.Auditoria.Add(new RegistroAuditoria { Secuencia = 999_999, FechaUtc = DateTime.UtcNow, Evento = "insertado_a_mano", Actor = "x", Recurso = "x", HashPrevio = "x", Hash = "x" });
            await db.SaveChangesAsync();
        }
        var res = await verificador.VerificarAsync(default);
        Assert.False(res.Valida);
        await Esperar(async () =>
        {
            using var scope = api.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<EscudoDbContext>().Hechos.AnyAsync(h => h.Tipo == TiposHecho.AlteracionAuditoria) ? "ok" : null;
        });
    }

    [Fact]
    public void Validador_rechaza_normas_ajenas_celulares_y_cifras_inventadas()
    {
        var m = new MaterialNarrable("alerta", """{"monto_cop":900000,"fecha_colombia":"2026-07-15 14:05:00","saldo":950000}""", new HashSet<string> { "R005" });
        ContenidoNarrativa N(string resumen, string norma = "R005") => new(resumen, [new PasoLinea("2026-07-15 14:05:00", "Transferencia por $ 900.000")],
            ["Monto de 900.000 frente a saldo de 950.000"], [new NormaAplicada(norma, "aplica")], "Llamar al cliente por un canal registrado.", "alta");

        ValidadorNarrativa.Validar(N("Transferencia de $ 900.000 COP el 2026-07-15."), m);
        Assert.Throws<NarracionInvalidaException>(() => ValidadorNarrativa.Validar(N("Transferencia de $ 1.250.000 COP."), m));
        Assert.Throws<NarracionInvalidaException>(() => ValidadorNarrativa.Validar(N("Transferencia de $ 900.000.", "R007"), m));
        Assert.Throws<NarracionInvalidaException>(() => ValidadorNarrativa.Validar(N("Llamar al 3001234567."), m));
    }

    private sealed class NarradorFalso : INarrador
    {
        public bool Inventar { get; set; }
        public string Modelo => "falso-llm";
        public Task<ContenidoNarrativa> NarrarAsync(MaterialNarrable m, CancellationToken ct)
        {
            var monto = Inventar ? "1.234.567" : "900.000";
            var normas = m.NormasPermitidas.Take(2).Select(c => new NormaAplicada(c, "aplica a la operación")).ToList();
            using var doc = JsonDocument.Parse(m.ExpedienteJson);
            var linea = doc.RootElement.GetProperty("hechos").EnumerateArray()
                .Select(h => new PasoLinea(h.GetProperty("fecha_colombia").GetString()!, h.GetProperty("tipo").GetString()!)).ToList();
            return Task.FromResult(new ContenidoNarrativa($"El cliente intentó enviar $ {monto} COP a un destino nuevo; el Escudo retuvo la operación.",
                linea, ["Destino nuevo"], normas, "Confirmar con el cliente por un canal registrado.", "alta"));
        }
    }

    private sealed class FabricaConNarrador(INarrador narrador) : FabricaApi
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => { s.RemoveAll<INarrador>(); s.AddSingleton(narrador); });
        }
    }
}

