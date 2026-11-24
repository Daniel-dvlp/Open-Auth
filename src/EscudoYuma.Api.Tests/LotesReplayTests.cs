using EscudoYuma.Api.Features.Sandbox.Replay;
using EscudoYuma.Api.Shared.Scoring;

namespace EscudoYuma.Api.Tests;

/// <summary>Lógica de lotes y orden del replay (<see cref="CanalLotes"/>) con un sandbox simulado en memoria.</summary>
public class LotesReplayTests
{
    /// <summary>Sandbox falso: entrega T0000, T0001... en orden y registra cada petición de lote.</summary>
    private sealed class SandboxFalso(int total, int retrasoMs = 0)
    {
        private int _cursor;
        public List<int> Pedidos { get; } = [];
        public async Task<LoteTransacciones> PedirAsync(int cantidad, CancellationToken ct)
        {
            Pedidos.Add(cantidad);
            if (retrasoMs > 0) await Task.Delay(retrasoMs, ct);
            var n = Math.Min(cantidad, total - _cursor);
            var lote = Enumerable.Range(_cursor, n).Select(i => FabricaApi.Tx("C1", id: $"T{i:D4}")).ToList();
            _cursor += n;
            return new LoteTransacciones(lote, total - _cursor);
        }
    }

    [Theory]
    [InlineData(0)] // secuencial
    [InlineData(1)] // pipeline con un lote por adelantado
    [InlineData(4)]
    public async Task Los_lotes_se_procesan_en_el_orden_de_entrega_y_no_superan_el_tamano(int prefetch)
    {
        var sandbox = new SandboxFalso(total: 53, retrasoMs: 1);
        var vistas = new List<string>();
        var tamanos = new List<int>();

        await CanalLotes.EjecutarAsync(sandbox.PedirAsync, async (lote, ct) =>
        {
            await Task.Delay(2, ct); // el consumidor es más lento: obliga al productor a esperar en el canal
            tamanos.Add(lote.Count);
            vistas.AddRange(lote.Select(t => t.IdTransaccion));
        }, tamanoLote: 5, maxTx: 0, prefetch, CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, 53).Select(i => $"T{i:D4}"), vistas); // todas, sin repetir y en orden cronológico
        Assert.All(tamanos, n => Assert.InRange(n, 1, 5));
        Assert.Equal(11, tamanos.Count); // 10 lotes de 5 + 1 de 3
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Respeta_el_maximo_de_transacciones_sin_pedir_de_mas(int prefetch)
    {
        var sandbox = new SandboxFalso(total: 100);
        var vistas = new List<string>();
        await CanalLotes.EjecutarAsync(sandbox.PedirAsync, (lote, _) => { vistas.AddRange(lote.Select(t => t.IdTransaccion)); return Task.CompletedTask; },
            tamanoLote: 10, maxTx: 25, prefetch, CancellationToken.None);

        Assert.Equal(25, vistas.Count);
        Assert.Equal([10, 10, 5], sandbox.Pedidos); // el último pedido se recorta: nunca se entregan transacciones que no se van a decidir
    }

    [Fact]
    public async Task Secuencial_no_pide_el_siguiente_lote_hasta_publicar_el_actual()
    {
        var sandbox = new SandboxFalso(total: 20);
        var eventos = new List<string>();
        await CanalLotes.EjecutarAsync(async (n, ct) => { eventos.Add("pedir"); return await sandbox.PedirAsync(n, ct); },
            (_, _) => { eventos.Add("publicar"); return Task.CompletedTask; }, tamanoLote: 10, maxTx: 0, prefetch: 0, CancellationToken.None);

        Assert.Equal(["pedir", "publicar", "pedir", "publicar"], eventos); // la espera de cada decision no incluye a otros lotes
    }

    [Fact]
    public async Task Si_el_consumidor_falla_el_productor_se_detiene_y_la_excepcion_se_propaga()
    {
        var sandbox = new SandboxFalso(total: 1_000, retrasoMs: 1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CanalLotes.EjecutarAsync(sandbox.PedirAsync,
            (_, _) => throw new InvalidOperationException("publicación fallida"), tamanoLote: 5, maxTx: 0, prefetch: 2, CancellationToken.None));
        Assert.Equal("publicación fallida", ex.Message);
        Assert.True(sandbox.Pedidos.Count < 20); // no siguió pidiendo lotes sin consumidor
    }

    [Fact]
    public async Task Si_el_sandbox_falla_en_el_productor_el_error_llega_al_llamador()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => CanalLotes.EjecutarAsync(
            (_, _) => throw new HttpRequestException("sandbox caído"), (_, _) => Task.CompletedTask, 5, 0, prefetch: 1, CancellationToken.None));
    }
}
