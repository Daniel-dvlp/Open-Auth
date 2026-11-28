using YumaBilletera.Api.Shared.Persistencia;

namespace YumaBilletera.Api.Shared.Contabilidad;

/// <summary>Regla de negocio violada en el libro (saldo insuficiente, cuenta no activa…). Se traduce a 409/422 en el endpoint.</summary>
public sealed class ReglaContableException(string codigo, string mensaje) : Exception(mensaje)
{
    public string Codigo { get; } = codigo;
}

/// <summary>Cuentas internas del libro mayor (no pertenecen a ningún cliente).</summary>
public static class CuentasInternas
{
    public const string Apertura = "APERTURA";

    /// <summary>Cuenta de compensación por tipo de destino externo: el dinero sale de Yuma hacia ese tipo de contraparte.</summary>
    public static string Puente(string tipoDestino) => "PUENTE-" + tipoDestino.ToUpperInvariant();

    public static readonly string[] TiposDestinoExternos = ["otra_entidad", "comercio", "servicio", "operador"];
}

/// <summary>
/// Libro mayor de doble partida. Cada asiento = un débito y un crédito por el mismo monto, así la suma de todos los saldos es siempre 0.
/// Opera sobre entidades rastreadas por el DbContext del request: el llamador hace SaveChanges, y el token de concurrencia de
/// <see cref="Cuenta.Version"/> garantiza que dos operaciones simultáneas sobre la misma cuenta no se pisen (la segunda falla y se reintenta).
/// Scoped (usa el DbContext del request).
/// </summary>
public sealed class LibroMayor(BilleteraDbContext db, TimeProvider reloj)
{
    public async Task<Cuenta> ObtenerAsync(string idCuenta, CancellationToken ct) =>
        await db.Cuentas.FirstOrDefaultAsync(c => c.IdCuenta == idCuenta, ct)
        ?? throw new ReglaContableException("cuenta_inexistente", "La cuenta no existe.");

    /// <summary>Retiene el monto del disponible mientras el Escudo decide o un analista revisa (el dinero no sale).</summary>
    public static void Reservar(Cuenta cuenta, long montoCop)
    {
        ValidarMonto(montoCop);
        if (cuenta.Estado != EstadosCuenta.Activa)
            throw new ReglaContableException("cuenta_no_activa", "La cuenta no está habilitada para enviar dinero.");
        if (cuenta.DisponibleCop < montoCop)
            throw new ReglaContableException("saldo_insuficiente", "El saldo disponible no alcanza para esta operación.");
        cuenta.ReservadoCop += montoCop;
        cuenta.Version = Guid.NewGuid();
    }

    /// <summary>Devuelve al disponible un monto reservado (operación rechazada o descartada).</summary>
    public static void LiberarReserva(Cuenta cuenta, long montoCop)
    {
        if (montoCop <= 0 || cuenta.ReservadoCop < montoCop)
            throw new InvalidOperationException("La reserva a liberar no coincide con la reservada.");
        cuenta.ReservadoCop -= montoCop;
        cuenta.Version = Guid.NewGuid();
    }

    /// <summary>
    /// Registra un asiento: débito en <paramref name="debito"/> y crédito en <paramref name="credito"/>.
    /// Con <paramref name="consumirReserva"/> el monto sale de lo reservado (transferencia aprobada tras su evaluación).
    /// </summary>
    public Guid Asentar(Cuenta debito, Cuenta credito, long montoCop, string concepto, string? idTransaccion, bool consumirReserva)
    {
        ValidarMonto(montoCop);
        if (debito.IdCuenta == credito.IdCuenta)
            throw new ReglaContableException("misma_cuenta", "El origen y el destino no pueden ser la misma cuenta.");
        if (credito.Tipo == TiposCuenta.Cliente && credito.Estado == EstadosCuenta.Bloqueada)
            throw new ReglaContableException("destino_bloqueado", "La cuenta de destino no puede recibir dinero.");

        if (consumirReserva)
        {
            if (debito.ReservadoCop < montoCop) throw new InvalidOperationException("El monto no estaba reservado.");
            debito.ReservadoCop -= montoCop;
        }
        else if (!debito.PuedeQuedarNegativa && debito.DisponibleCop < montoCop)
            throw new ReglaContableException("saldo_insuficiente", "El saldo disponible no alcanza para esta operación.");

        debito.SaldoCop -= montoCop;
        credito.SaldoCop += montoCop;
        if (!debito.PuedeQuedarNegativa && debito.SaldoCop < 0)
            throw new InvalidOperationException("Invariante violado: saldo negativo en una cuenta que no lo admite.");
        debito.Version = Guid.NewGuid();
        credito.Version = Guid.NewGuid();

        var ahora = reloj.GetUtcNow().UtcDateTime;
        var asiento = Guid.NewGuid();
        db.Movimientos.AddRange(
            new Movimiento
            {
                IdAsiento = asiento, IdCuenta = debito.IdCuenta, Tipo = TiposMovimiento.Debito, MontoCop = montoCop,
                SaldoDespuesCop = debito.SaldoCop, Concepto = concepto, IdTransaccion = idTransaccion, Contraparte = credito.IdCuenta, FechaUtc = ahora,
            },
            new Movimiento
            {
                IdAsiento = asiento, IdCuenta = credito.IdCuenta, Tipo = TiposMovimiento.Credito, MontoCop = montoCop,
                SaldoDespuesCop = credito.SaldoCop, Concepto = concepto, IdTransaccion = idTransaccion, Contraparte = debito.IdCuenta, FechaUtc = ahora,
            });
        return asiento;
    }

    private static void ValidarMonto(long montoCop)
    {
        if (montoCop <= 0) throw new ReglaContableException("monto_invalido", "El monto debe ser mayor que cero.");
    }
}
