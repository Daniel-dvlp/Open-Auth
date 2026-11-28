using System.Text.Json;
using YumaBilletera.Api.Shared.Contabilidad;
using YumaBilletera.Api.Shared.Persistencia;

namespace YumaBilletera.Api.Tests;

/// <summary>El seed solo usa los datos entregados (03_Datos). Si la carpeta no está disponible, las pruebas no aplican y terminan sin afirmar.</summary>
public class SeedTests
{
    [Fact]
    public void Saldo_inicial_es_el_ultimo_saldo_real_de_transacciones_csv()
    {
        if (FabricaBilletera.RutaDatos is null) return;
        using var f = FabricaBilletera.ConDatosDelCaso(3);
        _ = f.Server;
        f.ConBd(db =>
        {
            // Valores calculados directamente de transacciones.csv: saldo_antes_cop - monto_cop de la última operación de cada cliente.
            Assert.Equal(3_897_902, db.Cuentas.Single(c => c.IdCuenta == "C00001").SaldoCop);
            Assert.Equal(110_247, db.Cuentas.Single(c => c.IdCuenta == "C00002").SaldoCop);
            Assert.Equal(64_902, db.Cuentas.Single(c => c.IdCuenta == "C00003").SaldoCop);
            Assert.Equal(0, db.Cuentas.Sum(c => c.SaldoCop)); // la contrapartida es la cuenta de apertura
            Assert.Equal(-(3_897_902L + 110_247 + 64_902), db.Cuentas.Single(c => c.IdCuenta == CuentasInternas.Apertura).SaldoCop);
            return 0;
        });
    }

    [Fact]
    public void Clientes_con_datos_colombianos_del_caso_y_sin_edad_ni_etiquetas()
    {
        if (FabricaBilletera.RutaDatos is null) return;
        using var f = FabricaBilletera.ConDatosDelCaso(3);
        _ = f.Server;
        f.ConBd(db =>
        {
            var c1 = db.Clientes.Single(c => c.IdCliente == "C00001");
            Assert.Equal("3000000001", c1.Llave);            // celular sintético de 10 dígitos, mismo esquema del Escudo
            Assert.Equal("Antioquia", c1.Departamento);       // clientes.csv
            Assert.Equal("microcomerciante", c1.TipoCliente);
            Assert.Equal(new DateOnly(2023, 9, 14), c1.FechaVinculacion);
            Assert.True(db.HistorialDestinos.Any(h => h.IdCliente == "C00001"));
            Assert.True(db.Dispositivos.Any(d => d.IdCliente == "C00001" && d.IdDispositivo == "D19CE9F2"));
            Assert.DoesNotContain(typeof(Cliente).GetProperties(), p => p.Name.Contains("Edad") || p.Name.Contains("Mula"));
            return 0;
        });
    }

    [Fact]
    public void Credenciales_de_prueba_se_escriben_en_disco_con_el_dispositivo_habitual()
    {
        if (FabricaBilletera.RutaDatos is null) return;
        using var f = FabricaBilletera.ConDatosDelCaso(3);
        _ = f.Server;
        using var doc = JsonDocument.Parse(File.ReadAllText(f.RutaSalidaSeed));
        var c1 = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("id_cliente").GetString() == "C00001");
        Assert.Equal("D19CE9F2", c1.GetProperty("id_dispositivo").GetString()); // dispositivo de su última operación
        Assert.Equal(16, c1.GetProperty("clave").GetString()!.Length);
    }
}
