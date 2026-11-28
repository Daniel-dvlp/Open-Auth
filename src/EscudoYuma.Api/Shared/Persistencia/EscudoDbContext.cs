using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace EscudoYuma.Api.Shared.Persistencia;

/// <summary>DbContext Scoped (un contexto por request; no es thread-safe).</summary>
public class EscudoDbContext(DbContextOptions<EscudoDbContext> opciones) : DbContext(opciones), IBaseConAuditoria
{
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<DispositivoConocido> Dispositivos => Set<DispositivoConocido>();
    public DbSet<ReputacionCuenta> Reputaciones => Set<ReputacionCuenta>();
    public DbSet<Alerta> Alertas => Set<Alerta>();
    public DbSet<RegistroAuditoria> Auditoria => Set<RegistroAuditoria>();
    public DbSet<HechoExpediente> Hechos => Set<HechoExpediente>();
    public DbSet<NarrativaAlerta> Narrativas => Set<NarrativaAlerta>();

    private static ValueComparer<List<string>> ComparadorLista() => new(
        (a, b) => a != null && b != null && a.SequenceEqual(b),
        l => l.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
        l => l.ToList());

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Usuario>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Llave).IsUnique();
            e.HasIndex(x => x.IdCliente);
            e.Property(x => x.Llave).HasMaxLength(64);
            e.Property(x => x.IdCliente).HasMaxLength(32);
            e.Property(x => x.Rol).HasMaxLength(16);
            e.HasMany(x => x.Dispositivos).WithOne().HasForeignKey(d => d.UsuarioId).OnDelete(DeleteBehavior.Cascade);
        });
        mb.Entity<DispositivoConocido>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UsuarioId, x.IdDispositivo }).IsUnique();
            e.Property(x => x.IdDispositivo).HasMaxLength(64);
        });
        mb.Entity<ReputacionCuenta>(e =>
        {
            e.HasKey(x => x.IdCuenta);
            e.Property(x => x.IdCuenta).HasMaxLength(32);
            e.Property(x => x.Senales).HasConversion(l => JsonLista.Serializar(l), s => JsonLista.Deserializar(s), ComparadorLista());
            e.Property(x => x.SenalesAutomaticas).HasConversion(l => JsonLista.Serializar(l), s => JsonLista.Deserializar(s), ComparadorLista());
        });
        mb.Entity<Alerta>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.IdTransaccion).IsUnique();
            e.HasIndex(x => x.Estado);
            e.Property(x => x.Estado).IsConcurrencyToken(); // dos analistas no pueden resolver la misma alerta a la vez
            e.Property(x => x.Razones).HasConversion(l => JsonLista.Serializar(l), s => JsonLista.Deserializar(s), ComparadorLista());
        });
        mb.Entity<HechoExpediente>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.IdTransaccion, x.Id });
            e.HasIndex(x => new { x.IdCliente, x.FechaUtc });
            e.Property(x => x.Tipo).HasMaxLength(32);
            e.Property(x => x.Normas).HasConversion(l => JsonLista.Serializar(l), s => JsonLista.Deserializar(s), ComparadorLista());
        });
        mb.Entity<NarrativaAlerta>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.IdAlerta, x.CreadaUtc });
        });
        mb.ConfigurarAuditoria();
    }

    /// <summary>El expediente, como la auditoría, es append-only.</summary>
    private void ProtegerExpediente()
    {
        if (ChangeTracker.Entries<HechoExpediente>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("HechoExpediente es append-only: no admite modificación ni borrado.");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ChangeTracker.ProtegerAuditoria();
        ProtegerExpediente();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ChangeTracker.ProtegerAuditoria();
        ProtegerExpediente();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
