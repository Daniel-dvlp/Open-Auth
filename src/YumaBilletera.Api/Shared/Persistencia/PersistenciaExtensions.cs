using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using YumaBilletera.Api.Shared.Contabilidad;

namespace YumaBilletera.Api.Shared.Persistencia;

public static class PersistenciaExtensions
{
    /// <summary>Persistencia:Proveedor = postgres | memoria (lectura diferida para que las pruebas puedan sobrescribirla).</summary>
    public static IServiceCollection AddPersistencia(this IServiceCollection servicios)
    {
        servicios.AddDbContext<BilleteraDbContext>((sp, o) =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            if ((cfg["Persistencia:Proveedor"] ?? "postgres") == "memoria")
                o.UseInMemoryDatabase(cfg["Persistencia:NombreMemoria"] ?? "yuma-billetera");
            else
                o.UseNpgsql(cfg["Persistencia:Conexion"]
                    ?? throw new InvalidOperationException("Falta Persistencia:Conexion (usar variable de entorno Persistencia__Conexion)."));
        });
        servicios.AddSingleton<IPasswordHasher<Cliente>, PasswordHasher<Cliente>>();
        servicios.AddScoped<LibroMayor>();
        servicios.AddHostedService<InicializadorBd>();
        return servicios;
    }
}

/// <summary>Crea el esquema (EnsureCreated, igual que el Escudo) y, si se pide, siembra la billetera desde los CSV del caso.</summary>
public sealed class InicializadorBd(IServiceProvider sp, IConfiguration cfg, IHostEnvironment env, ILogger<InicializadorBd> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BilleteraDbContext>();
        for (var intento = 1; ; intento++)
        {
            try { await db.Database.EnsureCreatedAsync(ct); break; }
            catch (Exception) when (intento < 30 && !ct.IsCancellationRequested)
            {
                log.LogWarning("Base de datos no disponible (intento {Intento}/30); reintentando.", intento);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        if (!cfg.GetValue("Seed:Habilitado", false)) return;
        // El seed escribe claves de prueba en disco: nunca fuera de Development sin permiso explícito (mismo criterio del Escudo).
        if (!env.IsDevelopment() && !cfg.GetValue("Seed:PermitirFueraDeDesarrollo", false))
            throw new InvalidOperationException("Seed:Habilitado=true fuera de Development exige Seed:PermitirFueraDeDesarrollo=true (solo datos sintéticos).");
        var seeder = new SeederBilletera(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<Cliente>>(),
            scope.ServiceProvider.GetRequiredService<LibroMayor>(), cfg, env, scope.ServiceProvider.GetRequiredService<TimeProvider>(), log);
        await seeder.EjecutarAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Siembra la billetera SOLO con los datos entregados en el caso (03_Datos), sin inventar clientes ni saldos:
/// <list type="bullet">
/// <item>clientes.csv → clientes y cuentas (llave = celular sintético "3" + número del cliente en 9 dígitos, igual que el Escudo).</item>
/// <item>transacciones.csv → saldo inicial = saldo_antes_cop - monto_cop de la última operación del cliente (todas las operaciones del
/// caso son salidas); historial de destinos (destino_nuevo) y dispositivos usados.</item>
/// <item>eventos_app.csv → registros de dispositivo, cambios de clave y cambios de SIM.</item>
/// </list>
/// No se usan las etiquetas es_fraude, modalidad_fraude ni cuenta_mula_confirmada. Las claves de prueba se generan al azar y se escriben
/// en seed/billetera_usuarios_prueba.json (ignorado por git); nunca en logs.
/// </summary>
public sealed class SeederBilletera(BilleteraDbContext db, IPasswordHasher<Cliente> hasher, LibroMayor libro, IConfiguration cfg,
    IHostEnvironment env, TimeProvider reloj, ILogger log)
{
    private sealed record Credencial(string id_cliente, string llave, string clave, string id_dispositivo, string departamento, long saldo_inicial_cop);

    public async Task EjecutarAsync(CancellationToken ct)
    {
        if (await db.Clientes.AnyAsync(ct)) { log.LogInformation("Seed de billetera omitido: ya existen clientes."); return; }
        var datos = Resolver(cfg["Seed:RutaDatos"] ?? "../../../Caso_de_estudio_Yuma_Pay/03_Datos");
        var rutaClientes = Path.Combine(datos, "clientes.csv");
        if (!File.Exists(rutaClientes)) { log.LogWarning("Seed de billetera omitido: no se encontró clientes.csv en {Ruta}.", datos); return; }
        var max = cfg.GetValue("Seed:MaxClientes", 0); // 0 = todos (las pruebas siembran pocos)

        var filasClientes = LectorCsv.Leer(rutaClientes).ToList();
        if (max > 0) filasClientes = filasClientes.Take(max).ToList();
        var ids = filasClientes.Select(f => f["id_cliente"]).ToHashSet();

        // --- transacciones.csv: último saldo, destinos y dispositivos por cliente ---
        var ultimoSaldo = new Dictionary<string, (string Fecha, long Saldo)>();
        var destinos = new Dictionary<(string, string), DateTime>();
        var dispositivos = new Dictionary<(string, string), (DateTime Primero, string? So)>();
        var ultimoDispositivo = new Dictionary<string, (string Fecha, string Disp)>();
        var rutaTx = Path.Combine(datos, "transacciones.csv");
        if (File.Exists(rutaTx))
            foreach (var t in LectorCsv.Leer(rutaTx))
            {
                var c = t["id_cliente"];
                if (!ids.Contains(c)) continue;
                var fecha = t["fecha_hora"];
                var saldo = Math.Max(0, long.Parse(t["saldo_antes_cop"], CultureInfo.InvariantCulture) - long.Parse(t["monto_cop"], CultureInfo.InvariantCulture));
                if (!ultimoSaldo.TryGetValue(c, out var u) || string.CompareOrdinal(fecha, u.Fecha) >= 0) ultimoSaldo[c] = (fecha, saldo);
                var utc = HoraColombia.AUtc(fecha);
                var kd = (c, t["id_destino"]);
                if (!destinos.TryGetValue(kd, out var fd) || utc > fd) destinos[kd] = utc;
                var kv = (c, t["id_dispositivo"]);
                if (!dispositivos.TryGetValue(kv, out var dv) || utc < dv.Primero) dispositivos[kv] = (utc, dv.So);
                if (!ultimoDispositivo.TryGetValue(c, out var ud) || string.CompareOrdinal(fecha, ud.Fecha) >= 0) ultimoDispositivo[c] = (fecha, t["id_dispositivo"]);
            }

        // --- eventos_app.csv: dispositivos registrados, cambios de clave y de SIM ---
        var eventos = new List<EventoSeguridad>();
        var rutaEv = Path.Combine(datos, "eventos_app.csv");
        if (File.Exists(rutaEv))
            foreach (var e in LectorCsv.Leer(rutaEv))
            {
                var c = e["id_cliente"];
                if (!ids.Contains(c) || e["resultado"] != "exitoso") continue;
                var utc = HoraColombia.AUtc(e["fecha_hora"]);
                switch (e["tipo_evento"])
                {
                    case "registro_dispositivo":
                        var kv = (c, e["id_dispositivo"]);
                        var so = string.IsNullOrEmpty(e["sistema_operativo"]) ? null : e["sistema_operativo"];
                        dispositivos[kv] = dispositivos.TryGetValue(kv, out var dv) ? (utc < dv.Primero ? utc : dv.Primero, dv.So ?? so) : (utc, so);
                        break;
                    case TiposEventoSeguridad.CambioClave:
                    case TiposEventoSeguridad.CambioSim:
                        eventos.Add(new EventoSeguridad { IdCliente = c, Tipo = e["tipo_evento"], FechaUtc = utc });
                        break;
                }
            }

        var ahora = reloj.GetUtcNow().UtcDateTime;
        var apertura = new Cuenta { IdCuenta = CuentasInternas.Apertura, Tipo = TiposCuenta.Apertura, CreadaUtc = ahora };
        db.Cuentas.Add(apertura);
        foreach (var tipo in CuentasInternas.TiposDestinoExternos)
            db.Cuentas.Add(new Cuenta { IdCuenta = CuentasInternas.Puente(tipo), Tipo = TiposCuenta.Puente, CreadaUtc = ahora });

        var clientes = new Cliente[filasClientes.Count];
        var creds = new Credencial[filasClientes.Count];
        // PBKDF2 es costoso a propósito: se paraleliza el hash (igual que el seed del Escudo).
        Parallel.For(0, filasClientes.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var f = filasClientes[i];
            var id = f["id_cliente"];
            var llave = LlaveDe(id);
            var clave = ClaveAleatoria();
            var cli = new Cliente
            {
                IdCliente = id, Llave = llave, TipoCliente = f["tipo_cliente"], Departamento = f["departamento"],
                FechaVinculacion = DateOnly.ParseExact(f["fecha_vinculacion"], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                CanalVinculacion = f["canal_vinculacion"],
            };
            cli.HashClave = hasher.HashPassword(cli, clave);
            clientes[i] = cli;
            var saldo = ultimoSaldo.TryGetValue(id, out var s) ? s.Saldo : 0;
            var disp = ultimoDispositivo.TryGetValue(id, out var d) ? d.Disp : "";
            creds[i] = new Credencial(id, llave, clave, disp, cli.Departamento, saldo);
        });

        db.Clientes.AddRange(clientes);
        foreach (var cr in creds)
        {
            var cuenta = new Cuenta { IdCuenta = cr.id_cliente, IdCliente = cr.id_cliente, Tipo = TiposCuenta.Cliente, CreadaUtc = ahora };
            db.Cuentas.Add(cuenta);
            if (cr.saldo_inicial_cop > 0) libro.Asentar(apertura, cuenta, cr.saldo_inicial_cop, "apertura", null, consumirReserva: false);
        }
        db.HistorialDestinos.AddRange(destinos.Select(kv => new HistorialDestino { IdCliente = kv.Key.Item1, IdDestino = kv.Key.Item2, UltimaOperacionUtc = kv.Value }));
        db.Dispositivos.AddRange(dispositivos.Select(kv => new Dispositivo
        {
            IdCliente = kv.Key.Item1, IdDispositivo = kv.Key.Item2, SistemaOperativo = kv.Value.So, RegistradoUtc = kv.Value.Primero,
        }));
        db.EventosSeguridad.AddRange(eventos);
        await db.SaveChangesAsync(ct);

        var salida = Resolver(cfg["Seed:RutaSalida"] ?? "../../seed/billetera_usuarios_prueba.json");
        Directory.CreateDirectory(Path.GetDirectoryName(salida)!);
        await File.WriteAllTextAsync(salida, JsonSerializer.Serialize(creds, new JsonSerializerOptions { WriteIndented = true }), ct);
        log.LogInformation("Seed de billetera: {Clientes} clientes ({ConSaldo} con saldo de transacciones.csv), {Destinos} destinos, {Dispositivos} dispositivos, {Eventos} eventos de seguridad. Credenciales de prueba en {Ruta}.",
            clientes.Length, creds.Count(c => c.saldo_inicial_cop > 0), destinos.Count, dispositivos.Count, eventos.Count, salida);
    }

    /// <summary>Celular sintético de 10 dígitos: "3" + número del cliente en 9 dígitos (C01234 → 3000001234). Mismo esquema del Escudo.</summary>
    public static string LlaveDe(string idCliente) =>
        "3" + int.Parse(new string(idCliente.Where(char.IsDigit).ToArray()), CultureInfo.InvariantCulture).ToString("D9", CultureInfo.InvariantCulture);

    private string Resolver(string ruta) => Path.IsPathRooted(ruta) ? ruta : Path.GetFullPath(Path.Combine(env.ContentRootPath, ruta));

    private static string ClaveAleatoria()
    {
        const string alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(16, alfabeto, (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
        });
    }
}
