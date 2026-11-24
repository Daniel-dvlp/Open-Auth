using System.Globalization;
using System.Text.Json.Serialization;
using EscudoYuma.Api.Shared.Estado;

namespace EscudoYuma.Api.Shared.Scoring;

/// <summary>Esquema `Transaccion` del openapi del sandbox (snake_case vía la política JSON global).</summary>
public sealed record Transaccion(
    string IdTransaccion,
    string FechaHora,
    string IdCliente,
    string TipoOperacion,
    string Canal,
    long MontoCop,
    long SaldoAntesCop,
    string IdDestino,
    string TipoDestino,
    int DestinoNuevo,
    string IdDispositivo,
    int DispositivoNuevo,
    [property: JsonPropertyName("cambio_clave_24h")] int CambioClave24h,
    [property: JsonPropertyName("cambio_sim_48h")] int CambioSim48h,
    string DepartamentoIp,
    int IpDepartamentoDistinto,
    int AntiguedadCuentaDias)
{
    public const string FormatoFecha = "yyyy-MM-dd HH:mm:ss";

    public static readonly string[] Tipos =
    [
        "transferencia_inmediata", "pago_qr", "retiro_corresponsal", "retiro_cajero", "pago_servicios", "recarga_celular", "compra_tarjeta_virtual",
    ];

    /// <summary>Hora local de Colombia tal como la entrega el sandbox.</summary>
    [JsonIgnore]
    public DateTime FechaLocal => DateTime.ParseExact(FechaHora, FormatoFecha, CultureInfo.InvariantCulture);

    /// <summary>Segundos desde 1970-01-01 sobre la hora local (el "ts" de training/features.py).</summary>
    [JsonIgnore]
    public long Ts => Tiempo.AEpoch(FechaHora);

    /// <summary>Definición de reglas_vigentes.csv: salida = transferencia o retiro.</summary>
    [JsonIgnore]
    public bool EsSalida => TipoOperacion == "transferencia_inmediata" || TipoOperacion.StartsWith("retiro", StringComparison.Ordinal);
    [JsonIgnore]
    public bool EsTransferencia => TipoOperacion == "transferencia_inmediata";
    [JsonIgnore]
    public bool EsRetiro => TipoOperacion.StartsWith("retiro", StringComparison.Ordinal);
}
