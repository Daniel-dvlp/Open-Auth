using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EscudoYuma.Api.Shared.Persistencia;

public sealed class HashClaveOptions
{
    public const int PisoCarga = 10_000;
    public int Iteraciones { get; set; }
    public bool PerfilCarga { get; set; }
}

public static class PersistenciaExtensions
{
    /// <summary>
    /// Registra EF Core. Proveedor por configuración (lectura diferida para que las pruebas puedan sobrescribirla):
    /// Persistencia:Proveedor = postgres | memoria.
    /// </summary>
    public static IServiceCollection AddPersistencia(this IServiceCollection servicios)
    {
        servicios.AddDbContext<EscudoDbContext>((sp, o) =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var proveedor = cfg["Persistencia:Proveedor"] ?? "postgres";
            if (proveedor == "memoria")
                o.UseInMemoryDatabase(cfg["Persistencia:NombreMemoria"] ?? "escudo-yuma");
            else
                o.UseNpgsql(cfg["Persistencia:Conexion"]
                    ?? throw new InvalidOperationException("Falta Persistencia:Conexion (usar variable de entorno Persistencia__Conexion)."));
        });
        // Costo del hash de claves (PBKDF2). Seguridad:Hash:Iteraciones = 0 (por defecto) usa el de ASP.NET Identity (100.000, HMAC-SHA512).
        // SOLO el perfil de carga (docker-compose.carga.yml, base de datos aparte con usuarios sintéticos) lo baja, y exige además
        // Seguridad:PerfilCarga=true y un piso de 10.000: cualquier otra combinación impide que la aplicación arranque (falla cerrado).
        servicios.AddOptions<HashClaveOptions>().Configure<IConfiguration>((o, cfg) =>
            {
                o.Iteraciones = cfg.GetValue("Seguridad:Hash:Iteraciones", 0);
                o.PerfilCarga = cfg.GetValue("Seguridad:PerfilCarga", false);
            })
            .Validate(o => o.Iteraciones == 0 || (o.PerfilCarga && o.Iteraciones >= HashClaveOptions.PisoCarga),
                "Seguridad:Hash:Iteraciones solo se admite con Seguridad:PerfilCarga=true (perfil de carga) y >= 10000.")
            .ValidateOnStart();
        servicios.AddOptions<PasswordHasherOptions>().Configure<IOptions<HashClaveOptions>>((o, h) =>
        {
            if (h.Value.Iteraciones > 0) o.IterationCount = h.Value.Iteraciones;
        });
        servicios.AddSingleton<IPasswordHasher<Usuario>, PasswordHasher<Usuario>>();
        servicios.AddHostedService<InicializadorBd>();
        return servicios;
    }
}

/// <summary>Crea el esquema (hackatón: EnsureCreated en lugar de migraciones) y, si se pide, siembra usuarios.</summary>
public sealed class InicializadorBd(IServiceProvider sp, IConfiguration cfg, IHostEnvironment env, ILogger<InicializadorBd> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EscudoDbContext>();

        // Postgres puede tardar en aceptar conexiones al arrancar con docker compose: reintentos.
        for (var intento = 1; ; intento++)
        {
            try { await db.Database.EnsureCreatedAsync(ct); break; }
            catch (Exception) when (intento < 30 && !ct.IsCancellationRequested)
            {
                log.LogWarning("Base de datos no disponible (intento {Intento}/30); reintentando.", intento);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        if (cfg.GetValue("Seed:Habilitado", false))
        {
            // El seed crea cuentas (incl. analista/servicio) con claves de prueba que se escriben en disco: nunca fuera de Development sin permiso explícito.
            if (!env.IsDevelopment() && !cfg.GetValue("Seed:PermitirFueraDeDesarrollo", false))
                throw new InvalidOperationException("Seed:Habilitado=true fuera de Development exige Seed:PermitirFueraDeDesarrollo=true (solo datos sintéticos de demo).");
            await new SeederUsuarios(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>(), cfg, env, log).EjecutarAsync(ct);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Crea usuarios a partir de clientes.csv con claves de prueba aleatorias y escribe seed/usuarios_prueba.json
/// (ignorado por git). Las claves NUNCA se escriben en logs.
/// </summary>
public sealed class SeederUsuarios(EscudoDbContext db, IPasswordHasher<Usuario> hasher, IConfiguration cfg, IHostEnvironment env, ILogger log)
{
    private record Credencial(string id_cliente, string llave, string clave, string id_dispositivo, string rol);

    public async Task EjecutarAsync(CancellationToken ct)
    {
        if (await db.Usuarios.AnyAsync(ct)) { log.LogInformation("Seed omitido: ya existen usuarios."); return; }

        var rutaCsv = Resolver(cfg["Seed:RutaClientes"] ?? "../../../Caso_de_estudio_Yuma_Pay/03_Datos/clientes.csv");
        if (!File.Exists(rutaCsv)) { log.LogWarning("Seed omitido: no se encontró clientes.csv."); return; }
        var rutaSalida = Resolver(cfg["Seed:RutaSalida"] ?? "../../seed/usuarios_prueba.json");
        var max = cfg.GetValue("Seed:MaxUsuarios", 0); // 0 = todos

        var filas = (await File.ReadAllLinesAsync(rutaCsv, ct)).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(','));
        if (max > 0) filas = filas.Take(max);
        var clientes = filas.Select(c => (Id: c[0], Depto: c[2], Fecha: c[4])).ToList();

        var ahora = DateTime.UtcNow;
        var usuarios = new Usuario[clientes.Count];
        var creds = new Credencial[clientes.Count];
        // PBKDF2 es costoso a propósito: se paraleliza el hash de ~6000 usuarios.
        Parallel.For(0, clientes.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var c = clientes[i];
            var num = int.Parse(new string(c.Id.Where(char.IsDigit).ToArray()), CultureInfo.InvariantCulture);
            var llave = "3" + num.ToString("D9", CultureInfo.InvariantCulture); // celular sintético de 10 dígitos
            var clave = ClaveAleatoria();
            var dispositivo = "DISP-" + c.Id;
            var u = NuevoUsuario(c.Id, llave, clave, Roles.Cliente, c.Depto, DateOnly.TryParse(c.Fecha, CultureInfo.InvariantCulture, out var f) ? f : null);
            u.Dispositivos.Add(new DispositivoConocido { UsuarioId = u.Id, IdDispositivo = dispositivo, SistemaOperativo = "Android", RegistradoUtc = ahora });
            usuarios[i] = u;
            creds[i] = new Credencial(c.Id, llave, clave, dispositivo, Roles.Cliente);
        });

        var extra = new List<(Usuario U, Credencial C)>();
        // Seed:ClaveServicio (opcional, solo por variable de entorno): clave fija de servicio01 para que otro servicio del entorno de demo
        // (la billetera) pueda autenticarse sin leer este archivo. Se exige longitud mínima; nunca se escribe en logs.
        var claveServicio = cfg["Seed:ClaveServicio"];
        if (claveServicio is { Length: > 0 and < 16 }) throw new InvalidOperationException("Seed:ClaveServicio debe tener al menos 16 caracteres.");
        foreach (var (llave, rol) in new[] { ("analista01", Roles.Analista), ("analista02", Roles.Analista), ("servicio01", Roles.Servicio) })
        {
            var clave = rol == Roles.Servicio && !string.IsNullOrEmpty(claveServicio) ? claveServicio : ClaveAleatoria();
            var u = NuevoUsuario("STAFF-" + llave, llave, clave, rol, null, null);
            u.Dispositivos.Add(new DispositivoConocido { UsuarioId = u.Id, IdDispositivo = "DISP-" + llave, RegistradoUtc = ahora });
            extra.Add((u, new Credencial(u.IdCliente, llave, clave, "DISP-" + llave, rol)));
        }

        db.Usuarios.AddRange(usuarios);
        db.Usuarios.AddRange(extra.Select(e => e.U));
        await db.SaveChangesAsync(ct);

        Directory.CreateDirectory(Path.GetDirectoryName(rutaSalida)!);
        await File.WriteAllTextAsync(rutaSalida,
            JsonSerializer.Serialize(creds.Concat(extra.Select(e => e.C)), new JsonSerializerOptions { WriteIndented = true }), ct);
        log.LogInformation("Seed completado: {Total} usuarios. Credenciales de prueba en {Ruta}.", usuarios.Length + extra.Count, rutaSalida);
    }

    private Usuario NuevoUsuario(string idCliente, string llave, string clave, string rol, string? depto, DateOnly? fecha)
    {
        var u = new Usuario { IdCliente = idCliente, Llave = Usuario.NormalizarLlave(llave), Rol = rol, Departamento = depto, FechaVinculacion = fecha };
        u.HashClave = hasher.HashPassword(u, clave);
        return u;
    }

    private string Resolver(string ruta) =>
        Path.IsPathRooted(ruta) ? ruta : Path.GetFullPath(Path.Combine(env.ContentRootPath, ruta));

    private static string ClaveAleatoria()
    {
        const string alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(16, alfabeto, (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
        });
    }
}
