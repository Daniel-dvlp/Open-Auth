using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Yuma.Comun.Auditoria;

/// <summary>Registro append-only; cada registro incluye el hash SHA-256 del anterior (cadena de evidencia).</summary>
public class RegistroAuditoria
{
    public long Id { get; set; }
    public long Secuencia { get; set; }
    public DateTime FechaUtc { get; set; }
    public string Evento { get; set; } = "";
    public string Actor { get; set; } = "";
    public string IdCorrelacion { get; set; } = "";
    public string Recurso { get; set; } = "";
    /// <summary>JSON sin PII (llaves enmascaradas).</summary>
    public string Detalle { get; set; } = "{}";
    public string HashPrevio { get; set; } = "";
    public string Hash { get; set; } = "";
}

/// <summary>Contrato del DbContext que guarda la bitácora encadenada (Escudo y Billetera tienen cada uno la suya).</summary>
public interface IBaseConAuditoria
{
    DbSet<RegistroAuditoria> Auditoria { get; }
}

/// <summary>Configuración EF y protección append-only reutilizables por cualquier DbContext con bitácora.</summary>
public static class AuditoriaModelo
{
    public static void ConfigurarAuditoria(this ModelBuilder mb) => mb.Entity<RegistroAuditoria>(e =>
    {
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.Secuencia).IsUnique(); // dos escritores concurrentes no pueden bifurcar la cadena
        e.Property(x => x.HashPrevio).HasMaxLength(64);
        e.Property(x => x.Hash).HasMaxLength(64);
    });

    /// <summary>La auditoría es append-only: se rechaza cualquier UPDATE/DELETE desde la aplicación.</summary>
    public static void ProtegerAuditoria(this Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker tracker)
    {
        if (tracker.Entries<RegistroAuditoria>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("RegistroAuditoria es append-only: no admite modificación ni borrado.");
    }
}

/// <summary>Contexto de auditoría por request (Scoped): lo llena el middleware de correlación.</summary>
public sealed class ContextoAuditoria
{
    public string IdCorrelacion { get; set; } = Guid.NewGuid().ToString("N");
    public string? Ip { get; set; }
    /// <summary>Actor fijo para procesos sin usuario (p. ej. el replay del sandbox).</summary>
    public string? Actor { get; set; }
}

/// <summary>Eslabón de la cadena que respalda un registro (para enlazar hechos del expediente con su evidencia).</summary>
public sealed record EslabonAuditoria(long Secuencia, string Hash);

public sealed record ResultadoVerificacion(bool Valida, int Total, long? PrimeraSecuenciaRota);

/// <summary>Cálculo y verificación de la cadena de hashes (funciones puras, fáciles de probar).</summary>
public static class CadenaHash
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Calcular(RegistroAuditoria r)
    {
        var texto = string.Join('|', r.Secuencia, r.FechaUtc.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
            r.Evento, r.Actor, r.IdCorrelacion, r.Recurso, r.Detalle, r.HashPrevio);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto))).ToLowerInvariant();
    }

    /// <summary>Recorre la cadena ordenada por secuencia y detecta el primer registro alterado, eliminado o reordenado.</summary>
    public static ResultadoVerificacion Verificar(IEnumerable<RegistroAuditoria> ordenados)
    {
        var previo = Genesis;
        long esperada = 1;
        var total = 0;
        foreach (var r in ordenados)
        {
            total++;
            if (r.Secuencia != esperada || r.HashPrevio != previo || r.Hash != Calcular(r))
                return new ResultadoVerificacion(false, total, r.Secuencia);
            previo = r.Hash;
            esperada++;
        }
        return new ResultadoVerificacion(true, total, null);
    }
}

/// <summary>
/// Singleton: candado, último eslabón conocido de la cadena y cola de registros pendientes de persistir, POR instancia de aplicación
/// (no estático: cada app o prueba tiene su propia base). Evita releer el último registro en cada escritura.
/// El candado solo protege el cálculo del eslabón y la cola (microsegundos); la escritura a la BD NO se hace bajo candado.
/// </summary>
public sealed class EstadoCadenaAuditoria
{
    public SemaphoreSlim Candado { get; } = new(1, 1);
    public (long Secuencia, string Hash)? Ultimo { get; set; }
    /// <summary>Registros ya encadenados (en orden de secuencia) a la espera de su commit. Protegida por <see cref="Candado"/>.</summary>
    public List<(RegistroAuditoria Registro, TaskCompletionSource Hecho)> Pendientes { get; } = [];
    /// <summary>true mientras un escritor en segundo plano está vaciando <see cref="Pendientes"/>. Protegida por <see cref="Candado"/>.</summary>
    public bool Escribiendo { get; set; }
}

/// <summary>
/// Escribe en la bitácora encadenada. Scoped. El eslabón (secuencia + hash) se calcula bajo un candado breve y por instancia
/// (ver <see cref="EstadoCadenaAuditoria"/>); la persistencia es un COMMIT DE GRUPO: los registros que llegan mientras otro commit está en
/// curso se escriben juntos en una sola transacción, y cada llamador espera (await) a que SU registro esté confirmado en la BD.
/// Así el orden de la cadena es el de la secuencia y la durabilidad por petición se mantiene, pero el costo de un commit (fsync) se reparte:
/// con un candado por commit el rendimiento de toda la API quedaba limitado a ~1/(latencia de commit) por segundo.
/// Con varias réplicas el índice único sobre Secuencia hace fallar al escritor perdedor (no se bifurca la cadena).
/// Los llamadores deben haber guardado ya sus propios cambios: el commit usa un DbContext propio, no el del request.
/// </summary>
public abstract class ServicioAuditoriaBase<TDb>(TDb db, ContextoAuditoria ctx, IHttpContextAccessor http, TimeProvider reloj, EstadoCadenaAuditoria cadena,
    IServiceScopeFactory fabrica, ILogger log) where TDb : DbContext, IBaseConAuditoria
{
    private const int MaxPorCommit = 500;

    public async Task<EslabonAuditoria> RegistrarAsync(string evento, string recurso, object? detalle = null, string? actor = null, CancellationToken ct = default)
    {
        RegistroAuditoria? registro = null;
        actor ??= ctx.Actor ?? http.HttpContext?.User.FindFirst("sub")?.Value ?? "anonimo";
        var hecho = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var iniciarEscritor = false;

        await cadena.Candado.WaitAsync(ct);
        try
        {
            if (cadena.Ultimo is null)
            {
                var bd = await db.Auditoria.AsNoTracking().OrderByDescending(r => r.Secuencia)
                    .Select(r => new { r.Secuencia, r.Hash }).FirstOrDefaultAsync(ct);
                cadena.Ultimo = bd is null ? (0, CadenaHash.Genesis) : (bd.Secuencia, bd.Hash);
            }
            var ultimo = cadena.Ultimo.Value;
            var ahora = reloj.GetUtcNow().UtcDateTime;
            var reg = new RegistroAuditoria
            {
                Secuencia = ultimo.Secuencia + 1,
                FechaUtc = new DateTime(ahora.Ticks - ahora.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc),
                Evento = evento,
                Actor = actor,
                IdCorrelacion = ctx.IdCorrelacion,
                Recurso = recurso,
                Detalle = detalle is null ? "{}" : JsonSerializer.Serialize(detalle),
                HashPrevio = ultimo.Hash,
            };
            reg.Hash = CadenaHash.Calcular(reg);
            registro = reg;
            cadena.Ultimo = (reg.Secuencia, reg.Hash); // el siguiente eslabón ya puede encadenarse sobre este
            cadena.Pendientes.Add((reg, hecho));
            if (!cadena.Escribiendo) { cadena.Escribiendo = true; iniciarEscritor = true; }
        }
        finally { cadena.Candado.Release(); }

        if (iniciarEscritor) _ = Task.Run(VaciarAsync); // en segundo plano y con su propio scope: no depende de este request
        await hecho.Task;                                // devuelve (o lanza) cuando SU registro está confirmado
        return new EslabonAuditoria(registro!.Secuencia, registro.Hash);
    }

    /// <summary>Vacía la cola en commits de grupo, en orden de secuencia, hasta que no queda nada.</summary>
    private async Task VaciarAsync()
    {
        while (true)
        {
            List<(RegistroAuditoria Registro, TaskCompletionSource Hecho)> lote;
            await cadena.Candado.WaitAsync();
            try
            {
                if (cadena.Pendientes.Count == 0) { cadena.Escribiendo = false; return; }
                var n = Math.Min(MaxPorCommit, cadena.Pendientes.Count);
                lote = cadena.Pendientes.GetRange(0, n);
                cadena.Pendientes.RemoveRange(0, n);
            }
            finally { cadena.Candado.Release(); }

            try
            {
                using var scope = fabrica.CreateScope();
                var bd = scope.ServiceProvider.GetRequiredService<TDb>();
                bd.Auditoria.AddRange(lote.Select(x => x.Registro));
                await bd.SaveChangesAsync(CancellationToken.None);
                foreach (var (_, hecho) in lote) hecho.TrySetResult();
            }
            catch (Exception ex)
            {
                // La cadena en memoria quedó por delante de la BD (otra réplica avanzó, o falló la conexión): se descarta lo pendiente, que
                // estaba encadenado sobre registros no confirmados, y se relee el último eslabón de la BD en la próxima escritura.
                List<(RegistroAuditoria Registro, TaskCompletionSource Hecho)> resto;
                await cadena.Candado.WaitAsync();
                try { resto = [.. cadena.Pendientes]; cadena.Pendientes.Clear(); cadena.Ultimo = null; cadena.Escribiendo = false; }
                finally { cadena.Candado.Release(); }
                log.LogError(ex, "No se pudo persistir un commit de auditoría ({N} registros, {Resto} descartados por encadenados).", lote.Count, resto.Count);
                foreach (var (_, hecho) in lote.Concat(resto)) hecho.TrySetException(ex);
                return;
            }
        }
    }

    /// <summary>Verifica toda la cadena (TODO: paginar si la bitácora crece mucho).</summary>
    public async Task<ResultadoVerificacion> VerificarAsync(CancellationToken ct = default) =>
        CadenaHash.Verificar(await db.Auditoria.AsNoTracking().OrderBy(r => r.Secuencia).ToListAsync(ct));
}
