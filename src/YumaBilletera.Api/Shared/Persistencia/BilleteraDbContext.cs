using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace YumaBilletera.Api.Shared.Persistencia;

/// <summary>DbContext Scoped de la billetera (un contexto por request; no es thread-safe). Base separada de la del Escudo.</summary>
public class BilleteraDbContext(DbContextOptions<BilleteraDbContext> opciones) : DbContext(opciones), IBaseConAuditoria
{
    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<Cuenta> Cuentas => Set<Cuenta>();
    public DbSet<Movimiento> Movimientos => Set<Movimiento>();
    public DbSet<Transferencia> Transferencias => Set<Transferencia>();
    public DbSet<Destinatario> Destinatarios => Set<Destinatario>();
    public DbSet<HistorialDestino> HistorialDestinos => Set<HistorialDestino>();
    public DbSet<Dispositivo> Dispositivos => Set<Dispositivo>();
    public DbSet<EventoSeguridad> EventosSeguridad => Set<EventoSeguridad>();
    public DbSet<SesionCliente> Sesiones => Set<SesionCliente>();
    public DbSet<Verificacion> Verificaciones => Set<Verificacion>();
    public DbSet<EventoSaliente> Bandeja => Set<EventoSaliente>();
    public DbSet<RegistroAuditoria> Auditoria => Set<RegistroAuditoria>();

    private static ValueComparer<List<string>> ComparadorLista() => new(
        (a, b) => a != null && b != null && a.SequenceEqual(b),
        l => l.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
        l => l.ToList());

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Cliente>(e =>
        {
            e.HasKey(x => x.IdCliente);
            e.Property(x => x.IdCliente).HasMaxLength(32);
            e.HasIndex(x => x.Llave).IsUnique();
            e.Property(x => x.Llave).HasMaxLength(16);
            e.Property(x => x.Departamento).HasMaxLength(64);
        });
        mb.Entity<Cuenta>(e =>
        {
            e.HasKey(x => x.IdCuenta);
            e.Property(x => x.IdCuenta).HasMaxLength(32);
            e.HasIndex(x => x.IdCliente);
            e.Property(x => x.Version).IsConcurrencyToken(); // dos débitos simultáneos no pueden pisarse el saldo
            e.Ignore(x => x.DisponibleCop);
            e.Ignore(x => x.PuedeQuedarNegativa);
        });
        mb.Entity<Movimiento>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.IdCuenta, x.Id });
            e.HasIndex(x => x.IdAsiento);
        });
        mb.Entity<Transferencia>(e =>
        {
            e.HasKey(x => x.IdTransaccion);
            e.Property(x => x.IdTransaccion).HasMaxLength(32);
            e.HasIndex(x => new { x.IdClienteOrigen, x.ClaveIdempotencia }).IsUnique(); // reintentos de la app no duplican el envío
            e.HasIndex(x => new { x.IdClienteOrigen, x.CreadaUtc });
            e.HasIndex(x => x.Estado);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Razones).HasConversion(l => JsonLista.Serializar(l), s => JsonLista.Deserializar(s), ComparadorLista());
        });
        mb.Entity<Destinatario>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.IdCliente, x.IdDestino }).IsUnique();
        });
        mb.Entity<HistorialDestino>(e => e.HasKey(x => new { x.IdCliente, x.IdDestino }));
        mb.Entity<Dispositivo>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.IdCliente, x.IdDispositivo }).IsUnique();
        });
        mb.Entity<EventoSeguridad>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.IdCliente, x.Tipo, x.FechaUtc });
        });
        mb.Entity<SesionCliente>(e =>
        {
            e.HasKey(x => x.IdSesion);
            e.HasIndex(x => x.IdCliente);
        });
        mb.Entity<Verificacion>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Proposito, x.Referencia });
        });
        mb.Entity<EventoSaliente>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.EnviadoUtc);
        });
        mb.ConfigurarAuditoria();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Proteger();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Proteger();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Auditoría y libro mayor son append-only: ninguna línea se modifica ni se borra (las correcciones son asientos de reverso).</summary>
    private void Proteger()
    {
        ChangeTracker.ProtegerAuditoria();
        if (ChangeTracker.Entries<Movimiento>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Movimiento es append-only: las correcciones se hacen con un asiento de reverso.");
    }
}
