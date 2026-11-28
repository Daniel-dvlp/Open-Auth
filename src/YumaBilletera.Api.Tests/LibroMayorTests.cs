using Microsoft.Extensions.DependencyInjection;
using YumaBilletera.Api.Shared.Contabilidad;
using YumaBilletera.Api.Shared.Persistencia;

namespace YumaBilletera.Api.Tests;

public class LibroMayorTests
{
    private static async Task<FabricaBilletera> FabricaConCuentasAsync(long saldoA = 500_000)
    {
        var f = new FabricaBilletera();
        _ = f.Server; // arranca el host (crea el esquema)
        await f.ConBdAsync(async (db, sp) =>
        {
            var apertura = new Cuenta { IdCuenta = CuentasInternas.Apertura, Tipo = TiposCuenta.Apertura };
            var a = new Cuenta { IdCuenta = "C00001", IdCliente = "C00001" };
            var b = new Cuenta { IdCuenta = "C00002", IdCliente = "C00002" };
            db.Cuentas.AddRange(apertura, a, b, new Cuenta { IdCuenta = CuentasInternas.Puente("otra_entidad"), Tipo = TiposCuenta.Puente });
            sp.GetRequiredService<LibroMayor>().Asentar(apertura, a, saldoA, "apertura", null, consumirReserva: false);
            await db.SaveChangesAsync();
        });
        return f;
    }

    [Fact]
    public async Task Asiento_mantiene_la_doble_partida_y_suma_cero()
    {
        using var f = await FabricaConCuentasAsync();
        await f.ConBdAsync(async (db, sp) =>
        {
            var libro = sp.GetRequiredService<LibroMayor>();
            var a = await libro.ObtenerAsync("C00001", default);
            var b = await libro.ObtenerAsync("C00002", default);
            LibroMayor.Reservar(a, 120_000);
            libro.Asentar(a, b, 120_000, "transferencia", "T1", consumirReserva: true);
            await db.SaveChangesAsync();
        });
        f.ConBd(db =>
        {
            Assert.Equal(0, db.Cuentas.Sum(c => c.SaldoCop));
            Assert.Equal(380_000, db.Cuentas.Single(c => c.IdCuenta == "C00001").SaldoCop);
            Assert.Equal(0, db.Cuentas.Single(c => c.IdCuenta == "C00001").ReservadoCop);
            Assert.Equal(120_000, db.Cuentas.Single(c => c.IdCuenta == "C00002").SaldoCop);
            var lineas = db.Movimientos.Where(m => m.IdTransaccion == "T1").ToList();
            Assert.Equal(2, lineas.Count);
            Assert.Equal(lineas.Single(m => m.Tipo == TiposMovimiento.Debito).MontoCop, lineas.Single(m => m.Tipo == TiposMovimiento.Credito).MontoCop);
            return 0;
        });
    }

    [Fact]
    public async Task No_se_puede_enviar_mas_que_el_disponible_ni_lo_ya_reservado()
    {
        using var f = await FabricaConCuentasAsync(100_000);
        await f.ConBdAsync(async (db, sp) =>
        {
            var a = await sp.GetRequiredService<LibroMayor>().ObtenerAsync("C00001", default);
            Assert.Equal("saldo_insuficiente", Assert.Throws<ReglaContableException>(() => LibroMayor.Reservar(a, 100_001)).Codigo);
            LibroMayor.Reservar(a, 70_000);
            Assert.Equal("saldo_insuficiente", Assert.Throws<ReglaContableException>(() => LibroMayor.Reservar(a, 30_001)).Codigo);
            Assert.Equal(30_000, a.DisponibleCop);
        });
    }

    [Fact]
    public async Task Cuenta_congelada_no_envia()
    {
        using var f = await FabricaConCuentasAsync();
        await f.ConBdAsync(async (db, sp) =>
        {
            var a = await sp.GetRequiredService<LibroMayor>().ObtenerAsync("C00001", default);
            a.Estado = EstadosCuenta.Congelada;
            Assert.Equal("cuenta_no_activa", Assert.Throws<ReglaContableException>(() => LibroMayor.Reservar(a, 1_000)).Codigo);
        });
    }

    [Fact]
    public async Task Dos_debitos_simultaneos_sobre_la_misma_cuenta_no_se_pisan()
    {
        using var f = await FabricaConCuentasAsync(100_000);
        using var s1 = f.Services.CreateScope();
        using var s2 = f.Services.CreateScope();
        var db1 = s1.ServiceProvider.GetRequiredService<BilleteraDbContext>();
        var db2 = s2.ServiceProvider.GetRequiredService<BilleteraDbContext>();
        var a1 = await db1.Cuentas.SingleAsync(c => c.IdCuenta == "C00001");
        var a2 = await db2.Cuentas.SingleAsync(c => c.IdCuenta == "C00001");
        LibroMayor.Reservar(a1, 80_000);
        LibroMayor.Reservar(a2, 80_000); // cada contexto ve 100.000 disponibles
        await db1.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        Assert.Equal(80_000, f.ConBd(db => db.Cuentas.Single(c => c.IdCuenta == "C00001").ReservadoCop));
    }

    [Fact]
    public async Task El_libro_es_append_only()
    {
        using var f = await FabricaConCuentasAsync();
        await f.ConBdAsync(async (db, _) =>
        {
            var m = await db.Movimientos.FirstAsync();
            m.MontoCop = 1;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
    }
}
