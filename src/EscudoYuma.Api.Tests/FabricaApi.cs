using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EscudoYuma.Api.Features.Scoring.EvaluarTransaccion;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Scoring;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EscudoYuma.Api.Tests;

/// <summary>API en memoria (EF InMemory) con configuración de prueba; cada instancia tiene su propia base.</summary>
public class FabricaApi : WebApplicationFactory<Program>
{
    public const string ClaveJwtPrueba = "clave-jwt-solo-para-pruebas-0123456789-abcdef";
    public const string Clave = "Clave-De-Prueba-1";
    public const string Depto = "Antioquia";
    public static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly Dictionary<string, string?> _extra;

    public FabricaApi() : this(new Dictionary<string, string?>()) { }
    protected FabricaApi(Dictionary<string, string?> extra) => _extra = extra;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["Persistencia:Proveedor"] = "memoria",
            ["Persistencia:NombreMemoria"] = "pruebas-" + Guid.NewGuid(),
            ["Jwt:Clave"] = ClaveJwtPrueba,
            ["Seed:Habilitado"] = "false",
            ["Seguridad:Login:MaxIntentosPorLlave"] = "100",
            ["Seguridad:Login:MaxIntentosPorIp"] = "1000",
        };
        foreach (var kv in _extra) cfg[kv.Key] = kv.Value;
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
    }

    /// <summary>Crea (o reutiliza) un usuario con un dispositivo conocido.</summary>
    public async Task<string> SembrarAsync(string idCliente, string rol = Roles.Cliente, string? llave = null, string dispositivo = "DISP-1")
    {
        llave ??= "k-" + idCliente;
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EscudoDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>();
        var u = new Usuario { IdCliente = idCliente, Llave = Usuario.NormalizarLlave(llave), Rol = rol, Departamento = Depto };
        u.HashClave = hasher.HashPassword(u, Clave);
        u.Dispositivos.Add(new DispositivoConocido { UsuarioId = u.Id, IdDispositivo = dispositivo, RegistradoUtc = DateTime.UtcNow });
        db.Usuarios.Add(u);
        await db.SaveChangesAsync();
        return u.Llave;
    }

    public Task<HttpResponseMessage> LoginAsync(string llave, string clave = Clave, string dispositivo = "DISP-1", string depto = Depto) =>
        CreateClient().PostAsJsonAsync("/v1/auth/login", new { llave, clave, id_dispositivo = dispositivo, departamento_ip = depto });

    /// <summary>Siembra un usuario con el rol dado, inicia sesión y devuelve un cliente HTTP con el Bearer puesto.</summary>
    public async Task<HttpClient> ClienteAutenticadoAsync(string idCliente, string rol)
    {
        var llave = await SembrarAsync(idCliente, rol);
        var resp = await LoginAsync(llave);
        var token = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static JwtSecurityToken Leer(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token);

    /// <summary>
    /// Transacción "riesgosa" = vaciado a destino nuevo, dispositivo nuevo, cambio de clave y de SIM, IP distinta y cuenta reciente.
    /// Con los eventos de <see cref="PrepararRiesgoAsync"/> el modelo ONNX real la puntúa por encima del umbral de retención.
    /// </summary>
    public static Transaccion Tx(string idCliente, string tipo = "pago_qr", long monto = 20_000, long saldo = 1_000_000, bool riesgosa = false,
        string hora = "14:05:00", string? id = null, string idDestino = "EXT1", string tipoDestino = "otra_entidad") =>
        new(id ?? "T" + Guid.NewGuid().ToString("N")[..10], $"2026-07-15 {hora}", idCliente, riesgosa ? "transferencia_inmediata" : tipo, "app",
            monto, saldo, idDestino, tipoDestino, riesgosa ? 1 : 0, "DISP-1", riesgosa ? 1 : 0, riesgosa ? 1 : 0, riesgosa ? 1 : 0, Depto, riesgosa ? 1 : 0, riesgosa ? 5 : 400);

    /// <summary>Retener con el modelo real (p = 0,984) una vez enviados los eventos de riesgo del cliente.</summary>
    public static Transaccion TxRetener(string idCliente) => Tx(idCliente, monto: 900_000, saldo: 950_000, riesgosa: true);

    /// <summary>Envía (rol servicio) cambio de SIM, cambio de clave y registro de dispositivo recientes, anteriores a las transacciones de prueba.</summary>
    public static async Task PrepararRiesgoAsync(HttpClient servicio, string idCliente)
    {
        object Ev(string tipo, string hora) => new
        {
            id_evento = $"E-{idCliente}-{tipo}", fecha_hora = $"2026-07-15 {hora}", id_cliente = idCliente, tipo_evento = tipo,
            id_dispositivo = "DISP-1", sistema_operativo = "Android", departamento_ip = Depto, resultado = "exitoso",
        };
        var resp = await servicio.PostAsJsonAsync("/v1/eventos", new
        {
            eventos = new[] { Ev("cambio_sim_detectado", "13:50:00"), Ev("cambio_clave", "13:55:00"), Ev("registro_dispositivo", "14:00:00") },
        });
        resp.EnsureSuccessStatusCode();
    }
}
