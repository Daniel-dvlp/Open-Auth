using System.Threading.Channels;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Scoring;
using Microsoft.EntityFrameworkCore;

namespace EscudoYuma.Api.Shared.ReputacionCuentas;

/// <summary>Instantánea de la reputación calculada por el estado en línea tras una transacción.</summary>
public sealed record ActualizacionReputacion(string IdCuenta, double Reputacion, IReadOnlyList<string> Senales, DateTime AhoraUtc);

/// <summary>
/// Cola hacia el escritor de reputación (Singleton). El pipeline de scoring solo encola (no espera la base de datos):
/// la persistencia es asíncrona. Capacidad acotada: si el escritor se atrasa se descartan las instantáneas más viejas,
/// que es seguro porque cada una reemplaza a la anterior de la misma cuenta ("la última gana").
/// </summary>
public sealed class ColaReputacion
{
    private readonly Channel<ActualizacionReputacion> _canal = Channel.CreateBounded<ActualizacionReputacion>(
        new BoundedChannelOptions(100_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public ChannelReader<ActualizacionReputacion> Lector => _canal.Reader;
    public void Encolar(ActualizacionReputacion a) => _canal.Writer.TryWrite(a);
    public void Encolar(ReputacionCalculada r, DateTime ahoraUtc) => Encolar(new ActualizacionReputacion(r.IdCuenta, r.Reputacion, r.Senales, ahoraUtc));
}

/// <summary>
/// Escritor en segundo plano de <see cref="ReputacionCuenta"/> (BackgroundService, un solo lector). Agrupa por cuenta cada
/// ~100 ms y hace un upsert por lote en su propio scope (el DbContext es Scoped). Solo toca las columnas automáticas
/// (puntaje y señales automáticas): el ajuste y el bloqueo del analista los escribe únicamente ResolverAlerta.
/// </summary>
public sealed class EscritorReputacion(ColaReputacion cola, IServiceScopeFactory fabrica, ILogger<EscritorReputacion> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (await cola.Lector.WaitToReadAsync(ct))
            {
                await Persistir(Drenar(), ct);
                await Task.Delay(100, ct); // deja que se junten actualizaciones de las mismas cuentas
            }
        }
        catch (OperationCanceledException) { /* apagado */ }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        var resto = Drenar();
        if (resto.Count > 0) await Persistir(resto, CancellationToken.None); // no se pierde lo pendiente al apagar
    }

    private Dictionary<string, ActualizacionReputacion> Drenar()
    {
        var lote = new Dictionary<string, ActualizacionReputacion>();
        while (lote.Count < 5_000 && cola.Lector.TryRead(out var a)) lote[a.IdCuenta] = a;
        return lote;
    }

    private async Task Persistir(Dictionary<string, ActualizacionReputacion> lote, CancellationToken ct)
    {
        if (lote.Count == 0) return;
        for (var intento = 1; intento <= 3; intento++)
        {
            try
            {
                using var scope = fabrica.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EscudoDbContext>();
                var ids = lote.Keys.ToList();
                var existentes = (await db.Reputaciones.Where(r => ids.Contains(r.IdCuenta)).ToListAsync(ct)).ToDictionary(r => r.IdCuenta);
                foreach (var (id, a) in lote)
                {
                    if (!existentes.TryGetValue(id, out var rep))
                    {
                        rep = new ReputacionCuenta { IdCuenta = id };
                        db.Reputaciones.Add(rep);
                    }
                    rep.ActualizarAutomatica(a.Reputacion, a.Senales, a.AhoraUtc);
                }
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (intento < 3)
            {
                // Carrera con ResolverAlerta (misma cuenta insertada a la vez): se reintenta recargando.
                await Task.Delay(50 * intento, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "No se pudo persistir la reputación de {Cuentas} cuentas.", lote.Count);
                return;
            }
        }
    }
}

/// <summary>
/// Cuentas con bloqueo definitivo, en memoria (Singleton) para no consultar la BD en cada transacción.
/// Se carga al arrancar y solo <c>ResolverAlerta</c> la actualiza (junto con la BD). Con varias réplicas habría que invalidarla (Redis pub/sub).
/// </summary>
public sealed class RegistroBloqueos
{
    private readonly HashSet<string> _bloqueadas = new();
    private readonly object _candado = new();

    public bool Contiene(string idCuenta)
    {
        lock (_candado) return _bloqueadas.Contains(idCuenta);
    }

    public void Marcar(string idCuenta)
    {
        lock (_candado) _bloqueadas.Add(idCuenta);
    }

    public void Cargar(IEnumerable<string> cuentas)
    {
        lock (_candado) foreach (var c in cuentas) _bloqueadas.Add(c);
    }
}

/// <summary>
/// Al arrancar: precarga las fechas de vinculación (dato de clientes sembrado en la BD) y las cuentas bloqueadas, y deja
/// listos el modelo ONNX, la política y el explicador (falla rápido si falta un artefacto; la primera inferencia se calienta aquí).
/// </summary>
public sealed class PrecargaMotor(IServiceProvider sp, ILogger<PrecargaMotor> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var modelo = sp.GetRequiredService<IModeloRiesgoFactory>().Crear(TipoModelo.Transaccion);
        sp.GetRequiredService<PoliticaDecision>();
        sp.GetRequiredService<ExplicadorRazones>();
        modelo.Predecir(new AtributosTransaccion(new double[CatalogoAtributos.N], []));

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EscudoDbContext>();
        var vinculaciones = await db.Usuarios.AsNoTracking().Where(u => u.FechaVinculacion != null)
            .Select(u => new { u.IdCliente, Fecha = u.FechaVinculacion!.Value }).ToListAsync(ct);
        sp.GetRequiredService<IEstadoEnLinea>().CargarVinculaciones(
            vinculaciones.Select(v => KeyValuePair.Create(v.IdCliente, Tiempo.AEpoch(v.Fecha.ToDateTime(TimeOnly.MinValue)))));
        var bloqueadas = await db.Reputaciones.AsNoTracking().Where(r => r.BloqueoDefinitivo).Select(r => r.IdCuenta).ToListAsync(ct);
        sp.GetRequiredService<RegistroBloqueos>().Cargar(bloqueadas);
        log.LogInformation("Motor listo: {Vinculaciones} vinculaciones, {Bloqueadas} cuentas bloqueadas, modelo {Version}.",
            vinculaciones.Count, bloqueadas.Count, modelo.Version);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
