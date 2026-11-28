using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EscudoYuma.Api.Shared.Notificaciones;

/// <summary>Destino de los avisos de resolución (la billetera). Sin UrlWebhook configurada no se envía nada.</summary>
public sealed class NotificacionesOptions
{
    public string? UrlWebhook { get; set; }
    /// <summary>Secreto HMAC compartido con la billetera (>= 32 caracteres). Solo por variable de entorno / secretos.</summary>
    public string? SecretoWebhook { get; set; }
    public int MaxIntentos { get; set; } = 8;
}

/// <summary>Resolución de un analista que la billetera debe aplicar a la operación retenida.</summary>
public sealed record AvisoResolucion(
    string IdTransaccion, string IdCliente, string Resolucion, string Accion, bool BloqueoDefinitivo, DateTime ResueltaUtc);

/// <summary>Firma HMAC-SHA256 de "marca_tiempo.cuerpo" (cabeceras X-Yuma-Marca-Tiempo y X-Yuma-Firma: sha256=hex). Evita alteración y reenvío.</summary>
public static class FirmaWebhook
{
    public const string CabeceraFirma = "X-Yuma-Firma";
    public const string CabeceraMarca = "X-Yuma-Marca-Tiempo";

    public static string Firmar(string secreto, long marcaTiempo, string cuerpo) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secreto), Encoding.UTF8.GetBytes($"{marcaTiempo}.{cuerpo}"))).ToLowerInvariant();
}

/// <summary>
/// Singleton + BackgroundService: cola acotada de avisos de resolución hacia la billetera, con reintentos exponenciales. No bloquea al analista.
/// Si un aviso se pierde (reinicio), la billetera concilia con GET /v1/transacciones/{id}/decision.
/// </summary>
public sealed class NotificadorResoluciones(IHttpClientFactory http, Microsoft.Extensions.Options.IOptions<NotificacionesOptions> opciones,
    TimeProvider reloj, ILogger<NotificadorResoluciones> log) : BackgroundService
{
    public const string ClienteHttp = "webhook-billetera";
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly Channel<AvisoResolucion> _cola = Channel.CreateBounded<AvisoResolucion>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public bool Habilitado => !string.IsNullOrWhiteSpace(opciones.Value.UrlWebhook) && opciones.Value.SecretoWebhook is { Length: >= 32 };

    public void Encolar(AvisoResolucion aviso)
    {
        if (Habilitado) _cola.Writer.TryWrite(aviso);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var aviso in _cola.Reader.ReadAllAsync(ct))
        {
            for (var intento = 1; intento <= opciones.Value.MaxIntentos && !ct.IsCancellationRequested; intento++)
            {
                try
                {
                    if (await EnviarAsync(aviso, ct)) break;
                }
                catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
                {
                    log.LogWarning("Aviso de resolución {IdTransaccion} no entregado (intento {Intento}).", aviso.IdTransaccion, intento);
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, intento))), ct);
            }
        }
    }

    private async Task<bool> EnviarAsync(AvisoResolucion aviso, CancellationToken ct)
    {
        var cuerpo = JsonSerializer.Serialize(aviso, Snake);
        var marca = reloj.GetUtcNow().ToUnixTimeSeconds();
        using var req = new HttpRequestMessage(HttpMethod.Post, opciones.Value.UrlWebhook) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };
        req.Headers.Add(FirmaWebhook.CabeceraMarca, marca.ToString(System.Globalization.CultureInfo.InvariantCulture));
        req.Headers.Add(FirmaWebhook.CabeceraFirma, FirmaWebhook.Firmar(opciones.Value.SecretoWebhook!, marca, cuerpo));
        using var resp = await http.CreateClient(ClienteHttp).SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return true;
        log.LogWarning("La billetera respondió {Codigo} al aviso de {IdTransaccion}.", (int)resp.StatusCode, aviso.IdTransaccion);
        return (int)resp.StatusCode is >= 400 and < 500 and not 408 and not 429; // error del aviso: reintentar no lo arregla
    }
}

public static class NotificacionesExtensions
{
    public static IServiceCollection AddNotificacionesBilletera(this IServiceCollection s)
    {
        s.AddOptions<NotificacionesOptions>().BindConfiguration("Billetera");
        s.AddHttpClient(NotificadorResoluciones.ClienteHttp, c => c.Timeout = TimeSpan.FromSeconds(5));
        s.AddSingleton<NotificadorResoluciones>();
        s.AddHostedService(sp => sp.GetRequiredService<NotificadorResoluciones>());
        return s;
    }
}
