using EscudoYuma.Api.Shared.Scoring;

namespace EscudoYuma.Api.Shared.Reglas;

/// <summary>Contexto que ve cada regla: la transacción más las ventanas YA incluyendo la transacción en curso (igual que features.py).</summary>
public sealed record ContextoRegla(Transaccion Tx, int Hora, int SalidasEn60Min, long AcumuladoDia);

public sealed record ReglaDisparada(string IdRegla, string Severidad, string Razon);

/// <summary>Strategy: cada regla vigente (reglas_vigentes.csv) es una implementación registrada en el contenedor.</summary>
public interface IRegla
{
    string Id { get; }
    string Severidad { get; }
    string? Evaluar(ContextoRegla c); // null = no se dispara; texto = razón legible
}

public sealed class ReglaDelegada(string id, string severidad, Func<ContextoRegla, string?> evaluar) : IRegla
{
    public string Id { get; } = id;
    public string Severidad { get; } = severidad;
    public string? Evaluar(ContextoRegla c) => evaluar(c);
}

/// <summary>Catálogo Singleton (inmutable) de reglas; sus disparos son señales 0/1 R001-R012 del vector de atributos.</summary>
public sealed class CatalogoReglas(IEnumerable<IRegla> reglas)
{
    private readonly IRegla[] _reglas = reglas.ToArray();
    public int Total => _reglas.Length;

    public IReadOnlyList<ReglaDisparada> Evaluar(ContextoRegla c)
    {
        var res = new List<ReglaDisparada>();
        foreach (var r in _reglas)
            if (r.Evaluar(c) is { } razon) res.Add(new ReglaDisparada(r.Id, r.Severidad, razon));
        return res;
    }

    /// <summary>Reglas vigentes de reglas_vigentes.csv (R001..R012). Los topes en COP vienen de la condición publicada.</summary>
    public static IEnumerable<IRegla> ReglasVigentes()
    {
        static string Cop(long v) => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '.'); // 1.900.000
        yield return new ReglaDelegada("R001", "media", c => c.Tx.EsTransferencia && c.Tx.MontoCop >= 1_000_000 ? $"transferencia de monto alto (${Cop(c.Tx.MontoCop)})" : null);
        yield return new ReglaDelegada("R002", "alta", c => c.Tx.EsSalida && c.Tx.DispositivoNuevo == 1 && c.Tx.MontoCop >= 150_000 ? "salida desde un dispositivo registrado recientemente" : null);
        yield return new ReglaDelegada("R003", "media", c => c.Tx.EsSalida && c.Hora <= 4 && c.Tx.MontoCop >= 150_000 ? "operación de madrugada" : null);
        yield return new ReglaDelegada("R004", "media", c => c.Tx.EsSalida && c.SalidasEn60Min >= 4 ? $"{c.SalidasEn60Min} salidas en los últimos 60 minutos" : null);
        yield return new ReglaDelegada("R005", "alta", c => c.Tx.EsTransferencia && c.Tx.DestinoNuevo == 1 && c.Tx.MontoCop >= 100_000 && c.Tx.MontoCop >= 0.9 * c.Tx.SaldoAntesCop
            ? $"vaciado de saldo ({100.0 * c.Tx.MontoCop / Math.Max(1, c.Tx.SaldoAntesCop):0} % del saldo) a destino nuevo" : null);
        yield return new ReglaDelegada("R006", "alta", c => c.Tx.EsTransferencia && c.Tx.CambioClave24h == 1 && c.Tx.MontoCop >= 200_000 ? "transferencia tras cambio de clave en las últimas 24 horas" : null);
        yield return new ReglaDelegada("R007", "alta", c => c.Tx.EsSalida && c.Tx.CambioSim48h == 1 && c.Tx.MontoCop >= 200_000 ? "salida tras cambio de SIM en las últimas 48 horas" : null);
        yield return new ReglaDelegada("R008", "media", c => c.Tx.EsRetiro && c.Tx.AntiguedadCuentaDias < 15 && c.Tx.MontoCop >= 300_000 ? $"retiro desde cuenta de {c.Tx.AntiguedadCuentaDias} días de antigüedad" : null);
        yield return new ReglaDelegada("R009", "baja", c => c.Tx.EsSalida && c.Tx.IpDepartamentoDistinto == 1 && c.Tx.MontoCop >= 250_000 ? $"ubicación atípica ({c.Tx.DepartamentoIp})" : null);
        yield return new ReglaDelegada("R010", "media", c => c.Tx.EsSalida && c.AcumuladoDia >= 1_500_000 ? $"acumulado diario de salidas ${Cop(c.AcumuladoDia)}" : null);
        yield return new ReglaDelegada("R011", "baja", c => c.Tx.TipoOperacion == "pago_qr" && c.Tx.MontoCop >= 200_000 ? "pago QR de monto alto" : null);
        yield return new ReglaDelegada("R012", "media", c => c.Tx.EsRetiro && c.Tx.MontoCop >= 600_000 ? $"retiro de monto alto (${Cop(c.Tx.MontoCop)})" : null);
    }
}
