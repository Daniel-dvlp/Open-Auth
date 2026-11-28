using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YumaBilletera.Api.Shared.Contabilidad;
using YumaBilletera.Api.Shared.Escudo;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YumaBilletera.Api.Shared.Persistencia;

namespace YumaBilletera.Api.Tests;

/// <summary>Billetera en memoria (EF InMemory); cada instancia tiene su propia base. Con <see cref="ConDatosDelCaso"/> siembra desde 03_Datos.</summary>
public class FabricaBilletera : WebApplicationFactory<Program>
{
    public const string ClaveJwtPrueba = "clave-jwt-billetera-solo-pruebas-0123456789";
    public static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly Dictionary<string, string?> _extra;
    public string RutaSalidaSeed { get; } = Path.Combine(Path.GetTempPath(), "billetera-seed-" + Guid.NewGuid().ToString("N") + ".json");

    public const string Clave = "Clave-De-Prueba-1";
    public const string SecretoWebhook = "secreto-webhook-solo-pruebas-0123456789-abc";

    /// <summary>Ejecuta un ciclo del trabajador de integración (bandeja, reevaluación y conciliación).</summary>
    public Task CicloIntegracionAsync() =>
        Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<TrabajadorIntegracion>().Single().CicloAsync(default);
    public const string Depto = "Antioquia";

    /// <summary>Escudo simulado: decide lo que la prueba indique y guarda lo que recibió.</summary>
    public FakeEscudo Escudo { get; } = new();

    public FabricaBilletera() : this(new Dictionary<string, string?>()) { }
    public FabricaBilletera(Dictionary<string, string?> extra) => _extra = extra;

    /// <summary>Carpeta 03_Datos del caso (variable YUMA_DATOS o carpeta hermana del repositorio). null si no existe.</summary>
    public static string? RutaDatos
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("YUMA_DATOS");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EscudoYuma.sln"))) dir = dir.Parent;
            var ruta = dir is null ? null : Path.GetFullPath(Path.Combine(dir.FullName, "..", "Caso_de_estudio_Yuma_Pay", "03_Datos"));
            return ruta is not null && Directory.Exists(ruta) ? ruta : null;
        }
    }

    /// <summary>Fábrica sembrada con los primeros <paramref name="clientes"/> clientes reales del caso.</summary>
    public static FabricaBilletera ConDatosDelCaso(int clientes = 3, Dictionary<string, string?>? extra = null)
    {
        var cfg = new Dictionary<string, string?>
        {
            ["Seed:Habilitado"] = "true",
            ["Seed:RutaDatos"] = RutaDatos,
            ["Seed:MaxClientes"] = clientes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var kv in extra ?? new()) cfg[kv.Key] = kv.Value;
        return new FabricaBilletera(cfg);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        var cfg = new Dictionary<string, string?>
        {
            ["Persistencia:Proveedor"] = "memoria",
            ["Persistencia:NombreMemoria"] = "billetera-" + Guid.NewGuid(),
            ["Jwt:Clave"] = ClaveJwtPrueba,
            ["Seed:Habilitado"] = "false",
            ["Seed:RutaSalida"] = RutaSalidaSeed,
            ["Seguridad:Login:MaxIntentosPorLlave"] = "100",
            ["Seguridad:Login:MaxIntentosPorIp"] = "1000",
            ["Escudo:UrlBase"] = "http://escudo.prueba", // el adapter real se reemplaza por FakeEscudo
            ["Escudo:TrabajadorEnSegundoPlano"] = "false",
            ["Escudo:SecretoWebhook"] = SecretoWebhook,
        };
        foreach (var kv in _extra) cfg[kv.Key] = kv.Value;
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg));
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IEscudoCliente>();
            s.AddSingleton<IEscudoCliente>(Escudo);
            s.RemoveAll<IEscudoSincronizacion>();
            s.AddSingleton<IEscudoSincronizacion>(Escudo);
        });
    }

    /// <summary>Crea un cliente con cuenta, saldo (asiento de apertura) y un dispositivo registrado hace 30 días.</summary>
    public async Task SembrarClienteAsync(string idCliente, long saldo = 1_000_000, string depto = Depto, string dispositivo = "DISP-1",
        DateOnly? vinculacion = null)
    {
        await ConBdAsync(async (db, sp) =>
        {
            var apertura = await db.Cuentas.FirstOrDefaultAsync(c => c.IdCuenta == CuentasInternas.Apertura);
            if (apertura is null)
            {
                apertura = new Cuenta { IdCuenta = CuentasInternas.Apertura, Tipo = TiposCuenta.Apertura };
                db.Cuentas.Add(apertura);
                foreach (var t in CuentasInternas.TiposDestinoExternos)
                    db.Cuentas.Add(new Cuenta { IdCuenta = CuentasInternas.Puente(t), Tipo = TiposCuenta.Puente });
            }
            var cli = new Cliente
            {
                IdCliente = idCliente, Llave = YumaBilletera.Api.Shared.Persistencia.SeederBilletera.LlaveDe(idCliente), Departamento = depto,
                FechaVinculacion = vinculacion ?? new DateOnly(2023, 1, 10),
            };
            cli.HashClave = sp.GetRequiredService<IPasswordHasher<Cliente>>().HashPassword(cli, Clave);
            var cuenta = new Cuenta { IdCuenta = idCliente, IdCliente = idCliente };
            db.Clientes.Add(cli);
            db.Cuentas.Add(cuenta);
            db.Dispositivos.Add(new Dispositivo { IdCliente = idCliente, IdDispositivo = dispositivo, SistemaOperativo = "Android", RegistradoUtc = DateTime.UtcNow.AddDays(-30) });
            if (saldo > 0) sp.GetRequiredService<LibroMayor>().Asentar(apertura, cuenta, saldo, "apertura", null, consumirReserva: false);
            await db.SaveChangesAsync();
        });
    }

    public Task<HttpResponseMessage> LoginAsync(string idCliente, string clave = Clave, string dispositivo = "DISP-1", string depto = Depto) =>
        CreateClient().PostAsJsonAsync("/v1/auth/login", new
        {
            llave = YumaBilletera.Api.Shared.Persistencia.SeederBilletera.LlaveDe(idCliente), clave, id_dispositivo = dispositivo, departamento_ip = depto,
        });

    /// <summary>Ingreso con resultado "permitir" del Escudo simulado; devuelve un cliente HTTP con el Bearer puesto.</summary>
    public async Task<HttpClient> ClienteAutenticadoAsync(string idCliente, string depto = Depto, string dispositivo = "DISP-1")
    {
        var resp = await LoginAsync(idCliente, depto: depto, dispositivo: dispositivo);
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public T ConBd<T>(Func<BilleteraDbContext, T> f)
    {
        using var scope = Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<BilleteraDbContext>());
    }

    public async Task ConBdAsync(Func<BilleteraDbContext, IServiceProvider, Task> f)
    {
        using var scope = Services.CreateScope();
        await f(scope.ServiceProvider.GetRequiredService<BilleteraDbContext>(), scope.ServiceProvider);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(RutaSalidaSeed)) File.Delete(RutaSalidaSeed);
    }
}

/// <summary>Escudo simulado para pruebas: la decisión se configura por prueba y se registran las solicitudes recibidas.</summary>
public sealed class FakeEscudo : IEscudoCliente, IEscudoSincronizacion
{
    public System.Collections.Concurrent.ConcurrentQueue<EventoApp> Eventos { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<JsonElement> DesafiosFallidos { get; } = new();
    public Func<string, EstadoCasoEscudo?> Caso { get; set; } = _ => null;

    public Task EnviarEventosAsync(IReadOnlyList<EventoApp> eventos, CancellationToken ct)
    {
        if (Caido) throw new EscudoNoDisponibleException("caído (prueba)");
        foreach (var e in eventos) Eventos.Enqueue(e);
        return Task.CompletedTask;
    }

    public Task ReportarDesafioFallidoAsync(JsonElement carga, CancellationToken ct)
    {
        if (Caido) throw new EscudoNoDisponibleException("caído (prueba)");
        DesafiosFallidos.Enqueue(carga);
        return Task.CompletedTask;
    }

    public Task<EstadoCasoEscudo?> ConsultarCasoAsync(string idTransaccion, CancellationToken ct)
    {
        if (Caido) throw new EscudoNoDisponibleException("caído (prueba)");
        return Task.FromResult(Caso(idTransaccion));
    }

    public Func<TransaccionEscudo, DecisionEscudo> Decidir { get; set; } =
        _ => new DecisionEscudo("aprobar", "aprobar", 0.05, ["Operación habitual del cliente"], "fake-1");
    public Func<IngresoEscudo, ResultadoIngresoEscudo> Ingreso { get; set; } =
        _ => new ResultadoIngresoEscudo("permitir", 0.0, ["Sin señales de riesgo en el ingreso"]);
    public bool Caido { get; set; }
    public System.Collections.Concurrent.ConcurrentQueue<TransaccionEscudo> Recibidas { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<IngresoEscudo> Ingresos { get; } = new();

    public static DecisionEscudo D(string decision, string? interna = null) => new(decision, interna ?? decision, 0.5, ["razón de prueba"], "fake-1");

    public Task<DecisionEscudo> EvaluarTransaccionAsync(TransaccionEscudo tx, CancellationToken ct)
    {
        if (Caido) throw new EscudoNoDisponibleException("caído (prueba)");
        Recibidas.Enqueue(tx);
        return Task.FromResult(Decidir(tx));
    }

    public Task<ResultadoIngresoEscudo> EvaluarIngresoAsync(IngresoEscudo ingreso, CancellationToken ct)
    {
        if (Caido) throw new EscudoNoDisponibleException("caído (prueba)");
        Ingresos.Enqueue(ingreso);
        return Task.FromResult(Ingreso(ingreso));
    }
}
