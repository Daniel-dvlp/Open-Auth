using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using EscudoYuma.Api.Features.Auth.RegistrarDispositivo;
using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Observabilidad;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.Seguridad;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EscudoYuma.Api.Tests;

/// <summary>Fábrica con configuración adicional (y, opcionalmente, otro entorno) para probar el arranque y los controles de seguridad.</summary>
public sealed class FabricaApiConfig(Dictionary<string, string?> extra, string? entorno = null, string? ipRemota = null) : FabricaApi(extra)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (entorno is not null) builder.UseEnvironment(entorno);
        // TestServer no tiene socket: se fija la IP del par (la que vería Kestrel) antes de todo el pipeline.
        if (ipRemota is not null)
            builder.ConfigureServices(s => s.AddTransient<IStartupFilter>(_ => new FijarIpRemota(System.Net.IPAddress.Parse(ipRemota))));
    }

    private sealed class FijarIpRemota(System.Net.IPAddress ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> siguiente) => app =>
        {
            app.Use((ctx, next) => { ctx.Connection.RemoteIpAddress = ip; return next(ctx); });
            siguiente(app);
        };
    }
}

internal sealed class RelojFalso(DateTimeOffset inicio) : TimeProvider
{
    private DateTimeOffset _ahora = inicio;
    public override DateTimeOffset GetUtcNow() => _ahora;
    public void Avanzar(TimeSpan t) => _ahora += t;
}

internal sealed class EntornoFalso(string nombre) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = nombre;
    public string ApplicationName { get; set; } = "prueba";
    public string ContentRootPath { get; set; } = ".";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

// ---------------------------------------------------------------------------------------------------------------- JWT
public class JwtSeguridadTests(FabricaApi api) : IClassFixture<FabricaApi>
{
    private static string Token(string alg, string clave = FabricaApi.ClaveJwtPrueba, string rol = "cliente", string sub = "C-JWT",
        string audiencia = "escudo-yuma-clientes", string emisor = "escudo-yuma", DateTime? expira = null)
    {
        var cred = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(clave)), alg);
        var ahora = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(emisor, audiencia,
            [new Claim("sub", sub), new Claim("rol", rol), new Claim("jti", Guid.NewGuid().ToString("N"))],
            ahora.AddMinutes(-30), expira ?? ahora.AddMinutes(10), cred);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private async Task<HttpStatusCode> LlamarAsync(string token)
    {
        var c = api.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await c.GetAsync("/v1/cuentas/C-JWT/reputacion")).StatusCode;
    }

    [Fact]
    public async Task Un_token_HS256_valido_se_acepta()
    {
        await api.SembrarAsync("C-JWT");
        Assert.Equal(HttpStatusCode.OK, await LlamarAsync(Token(SecurityAlgorithms.HmacSha256)));
    }

    [Fact]
    public async Task Algoritmo_HS512_con_la_misma_clave_es_rechazado()
    {
        // La clave de prueba mide 45 bytes (360 bits): HS512 exige >= 512 bits para firmar, así que se firma a mano con la misma clave.
        var cabecera = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS512\",\"typ\":\"JWT\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var cuerpo = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{{\"sub\":\"C-JWT\",\"rol\":\"analista\",\"iss\":\"escudo-yuma\",\"aud\":\"escudo-yuma-clientes\",\"exp\":{exp}}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var hmac = new System.Security.Cryptography.HMACSHA512(Encoding.UTF8.GetBytes(FabricaApi.ClaveJwtPrueba));
        var firma = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{cabecera}.{cuerpo}"))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(HttpStatusCode.Unauthorized, await LlamarAsync($"{cabecera}.{cuerpo}.{firma}"));
    }

    [Fact]
    public async Task Token_sin_firma_alg_none_es_rechazado()
    {
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var sinFirma = $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{B64($"{{\"sub\":\"C-JWT\",\"rol\":\"analista\",\"iss\":\"escudo-yuma\",\"aud\":\"escudo-yuma-clientes\",\"exp\":{exp}}}")}.";
        Assert.Equal(HttpStatusCode.Unauthorized, await LlamarAsync(sinFirma));
    }

    [Fact]
    public async Task Clave_equivocada_audiencia_emisor_o_vencimiento_invalidos_dan_401()
    {
        await api.SembrarAsync("C-JWT");
        Assert.Equal(HttpStatusCode.Unauthorized, await LlamarAsync(Token(SecurityAlgorithms.HmacSha256, clave: "otra-clave-distinta-de-32-caracteres-o-mas-xyz")));
        Assert.Equal(HttpStatusCode.Unauthorized, await LlamarAsync(Token(SecurityAlgorithms.HmacSha256, audiencia: "otra")));
        Assert.Equal(HttpStatusCode.Unauthorized, await LlamarAsync(Token(SecurityAlgorithms.HmacSha256, emisor: "otro")));
        Assert.Equal(HttpStatusCode.Unauthorized, await LlamarAsync(Token(SecurityAlgorithms.HmacSha256, expira: DateTime.UtcNow.AddMinutes(-5)))); // más allá de los 30 s de tolerancia
    }

    [Fact]
    public void La_clave_JWT_debe_tener_32_caracteres_y_fuera_de_Development_no_ser_trivial_ni_faltar()
    {
        JwtEmisor Crear(string? clave, string entorno) =>
            new(Options.Create(new JwtOptions { Clave = clave }), new EntornoFalso(entorno), NullLogger<JwtEmisor>.Instance);

        Assert.Throws<InvalidOperationException>(() => Crear("corta", Environments.Production));
        Assert.Throws<InvalidOperationException>(() => Crear(new string('a', 64), Environments.Production));     // trivial
        Assert.Throws<InvalidOperationException>(() => Crear(null, Environments.Production));                   // Development no se cuela a producción
        Assert.Throws<InvalidOperationException>(() => Crear("   ", Environments.Staging));
        Assert.NotNull(Crear(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)), Environments.Production).LlaveFirma);
        Assert.NotNull(Crear(null, Environments.Development).LlaveFirma); // solo en Development hay clave efímera
    }
}

// ----------------------------------------------------------------------------------------------- roles / BOLA / defaults
public class AutorizacionTests(FabricaApi api) : IClassFixture<FabricaApi>
{
    private static HttpClient ConToken(FabricaApi api, string rol, string sub)
    {
        var cred = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(FabricaApi.ClaveJwtPrueba)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken("escudo-yuma", "escudo-yuma-clientes", [new Claim("sub", sub), new Claim("rol", rol)], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), cred);
        var c = api.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        return c;
    }

    [Fact]
    public async Task Un_rol_desconocido_solo_ve_su_propia_reputacion_BOLA_deniega_por_defecto()
    {
        await api.SembrarAsync("C-PROPIA");
        await api.SembrarAsync("C-AJENA");
        var raro = ConToken(api, "visitante", "C-PROPIA");
        Assert.Equal(HttpStatusCode.OK, (await raro.GetAsync("/v1/cuentas/C-PROPIA/reputacion")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await raro.GetAsync("/v1/cuentas/C-AJENA/reputacion")).StatusCode);
        var sinRol = ConToken(api, "", "C-PROPIA");
        Assert.Equal(HttpStatusCode.Forbidden, (await sinRol.GetAsync("/v1/cuentas/C-AJENA/reputacion")).StatusCode);
        // Comparación exacta (sin normalizar mayúsculas): "c-propia" no es "C-PROPIA".
        Assert.Equal(HttpStatusCode.Forbidden, (await ConToken(api, "cliente", "c-propia").GetAsync("/v1/cuentas/C-PROPIA/reputacion")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ConToken(api, Roles.Analista, "ANA").GetAsync("/v1/cuentas/C-AJENA/reputacion")).StatusCode);
    }

    [Fact]
    public async Task Matriz_de_roles_por_endpoint_anonimo_401_cliente_403_y_servicio_vs_analista()
    {
        var anonimo = api.CreateClient();
        var cliente = ConToken(api, Roles.Cliente, "C-ROL");
        var analista = ConToken(api, Roles.Analista, "ANA-ROL");
        var servicio = ConToken(api, Roles.Servicio, "SVC-ROL");
        var cuerpoEventos = JsonContent.Create(new { eventos = Array.Empty<object>() });

        async Task<HttpStatusCode> Estado(HttpClient c, HttpMethod m, string ruta, HttpContent? cuerpo = null)
        {
            using var resp = await c.SendAsync(new HttpRequestMessage(m, ruta) { Content = cuerpo });
            return resp.StatusCode;
        }

        foreach (var (metodo, ruta) in new[] { (HttpMethod.Get, "/v1/alertas"), (HttpMethod.Get, "/v1/sandbox/replay"), (HttpMethod.Post, "/v1/sandbox/replay"),
                     (HttpMethod.Post, "/v1/eventos"), (HttpMethod.Post, "/v1/score/transaccion"), (HttpMethod.Get, "/v1/cuentas/X/reputacion"),
                     (HttpMethod.Post, "/v1/auth/dispositivos"), (HttpMethod.Post, $"/v1/alertas/{Guid.NewGuid()}/resolver") })
            Assert.Equal(HttpStatusCode.Unauthorized, await Estado(anonimo, metodo, ruta, JsonContent.Create(new { })));

        // Replay y cola de alertas: solo analista. Un cliente y un servicio reciben 403 (replay no queda expuesto sin rol).
        Assert.Equal(HttpStatusCode.Forbidden, await Estado(cliente, HttpMethod.Post, "/v1/sandbox/replay"));
        Assert.Equal(HttpStatusCode.Forbidden, await Estado(servicio, HttpMethod.Post, "/v1/sandbox/replay"));
        Assert.Equal(HttpStatusCode.Forbidden, await Estado(servicio, HttpMethod.Get, "/v1/alertas"));
        Assert.Equal(HttpStatusCode.Forbidden, await Estado(cliente, HttpMethod.Get, "/v1/alertas"));
        // Replay deshabilitado por defecto (bandera): incluso el analista recibe 409.
        Assert.Equal(HttpStatusCode.Conflict, await Estado(analista, HttpMethod.Post, "/v1/sandbox/replay"));
        // Ingesta de eventos: solo rol servicio (ni cliente ni analista pueden mover el estado en línea).
        Assert.Equal(HttpStatusCode.Forbidden, await Estado(cliente, HttpMethod.Post, "/v1/eventos", cuerpoEventos));
        Assert.Equal(HttpStatusCode.Forbidden, await Estado(analista, HttpMethod.Post, "/v1/eventos", JsonContent.Create(new { eventos = Array.Empty<object>() })));
        // Resolver alertas (única vía de bloqueo definitivo): solo analista.
        Assert.Equal(HttpStatusCode.Forbidden, await Estado(servicio, HttpMethod.Post, $"/v1/alertas/{Guid.NewGuid()}/resolver", JsonContent.Create(new { resolucion = "fraude_confirmado", accion = "bloquear_cuenta" })));
        Assert.Equal(HttpStatusCode.Forbidden, await Estado(cliente, HttpMethod.Post, $"/v1/alertas/{Guid.NewGuid()}/resolver", JsonContent.Create(new { resolucion = "fraude_confirmado", accion = "bloquear_cuenta" })));
        Assert.Equal(HttpStatusCode.Forbidden, await Estado(cliente, HttpMethod.Post, "/v1/score/transaccion", JsonContent.Create(new { })));
    }

    [Fact]
    public async Task Cabeceras_seguras_en_respuestas_normales_y_de_error()
    {
        foreach (var ruta in new[] { "/v1/salud", "/v1/alertas", "/no-existe" })
        {
            using var resp = await api.CreateClient().GetAsync(ruta);
            Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", resp.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-store", resp.Headers.GetValues("Cache-Control").Single());
            Assert.Contains("default-src 'none'", resp.Headers.GetValues("Content-Security-Policy").Single());
            Assert.False(resp.Headers.Contains("Server"));
            Assert.False(resp.Headers.Contains("Access-Control-Allow-Origin")); // sin CORS: ningún origen de navegador autorizado
        }
    }

    [Fact]
    public async Task Los_errores_inesperados_no_filtran_detalles_internos()
    {
        var servicios = new ServiceCollection().AddScoped<ContextoAuditoria>().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = servicios };
        http.Response.Body = new MemoryStream();
        var manejador = new ManejadorErrores(NullLogger<ManejadorErrores>.Instance);

        Assert.True(await manejador.TryHandleAsync(http, new InvalidOperationException("Password=SECRETO en Host=db-interna\n   at Foo.Bar()"), default));

        http.Response.Body.Position = 0;
        var texto = await new StreamReader(http.Response.Body).ReadToEndAsync();
        Assert.Equal(500, http.Response.StatusCode);
        Assert.DoesNotContain("SECRETO", texto);
        Assert.DoesNotContain("db-interna", texto);
        Assert.DoesNotContain("   at ", texto);
        Assert.Contains("id_correlacion", texto);
    }
}

// ---------------------------------------------------------------------------------------------- arranque / configuración
public class ConfiguracionSeguraTests
{
    [Fact]
    public void Por_defecto_el_hash_de_claves_usa_las_iteraciones_de_Identity()
    {
        using var f = new FabricaApi();
        f.CreateClient();
        Assert.Equal(100_000, f.Services.GetRequiredService<IOptions<PasswordHasherOptions>>().Value.IterationCount);
    }

    [Fact]
    public void Bajar_las_iteraciones_sin_el_perfil_de_carga_impide_arrancar()
    {
        using var f = new FabricaApiConfig(new() { ["Seguridad:Hash:Iteraciones"] = "1000" });
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
        using var g = new FabricaApiConfig(new() { ["Seguridad:Hash:Iteraciones"] = "10000" }); // sin PerfilCarga
        Assert.ThrowsAny<Exception>(() => g.CreateClient());
        using var h = new FabricaApiConfig(new() { ["Seguridad:Hash:Iteraciones"] = "50", ["Seguridad:PerfilCarga"] = "true" }); // bajo el piso
        Assert.ThrowsAny<Exception>(() => h.CreateClient());
    }

    [Fact]
    public void Solo_el_perfil_de_carga_explicito_baja_las_iteraciones()
    {
        using var f = new FabricaApiConfig(new() { ["Seguridad:Hash:Iteraciones"] = "10000", ["Seguridad:PerfilCarga"] = "true" });
        f.CreateClient();
        Assert.Equal(10_000, f.Services.GetRequiredService<IOptions<PasswordHasherOptions>>().Value.IterationCount);
    }

    [Fact]
    public void El_compose_base_nunca_baja_las_iteraciones_y_solo_el_de_carga_activa_el_perfil()
    {
        var raiz = Raiz();
        Assert.DoesNotContain("Iteraciones", File.ReadAllText(Path.Combine(raiz, "docker-compose.yml")));
        Assert.DoesNotContain("Iteraciones", File.ReadAllText(Path.Combine(raiz, "docker-compose.tls.yml")));
        var carga = File.ReadAllText(Path.Combine(raiz, "docker-compose.carga.yml"));
        Assert.Contains("Seguridad__PerfilCarga", carga);
        Assert.DoesNotContain("Iteraciones", File.ReadAllText(Path.Combine(raiz, "src", "EscudoYuma.Api", "appsettings.json")));
        Assert.DoesNotContain("Iteraciones", File.ReadAllText(Path.Combine(raiz, "src", "EscudoYuma.Api", "appsettings.Development.json")));
    }

    [Fact]
    public void El_seed_fuera_de_Development_exige_permiso_explicito()
    {
        var baseCfg = new Dictionary<string, string?> { ["Seed:Habilitado"] = "true", ["Seguridad:ForzarHttps"] = "false" };
        using var sinPermiso = new FabricaApiConfig(baseCfg, Environments.Production);
        Assert.ThrowsAny<Exception>(() => sinPermiso.CreateClient());

        using var conPermiso = new FabricaApiConfig(new(baseCfg) { ["Seed:PermitirFueraDeDesarrollo"] = "true", ["Seed:RutaClientes"] = "no-existe.csv" }, Environments.Production);
        conPermiso.CreateClient(); // arranca (sin csv no siembra nada)
    }

    private static string Raiz()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "EscudoYuma.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("No se encontró EscudoYuma.sln");
    }

    [Fact]
    public void Docker_usuario_no_root_secretos_por_entorno_y_servicios_internos_sin_puertos_publicados()
    {
        var raiz = Raiz();
        var dockerfile = File.ReadAllText(Path.Combine(raiz, "Dockerfile"));
        Assert.Contains("USER $APP_UID", dockerfile);
        Assert.DoesNotMatch(@"FROM [^\n]*:(latest|8\.0)\s", dockerfile + "\n");
        var compose = File.ReadAllText(Path.Combine(raiz, "docker-compose.yml")).Replace("\r\n", "\n");
        Assert.Contains("${REDIS_PASSWORD:?", compose);
        Assert.Contains("--requirepass", compose);
        Assert.Contains("${JWT_CLAVE:?", compose);
        Assert.Matches(@"image: postgres:\d+\.\d+", compose);   // versión fija, no "latest" ni solo mayor
        Assert.Matches(@"image: redis:\d+\.\d+\.\d+", compose);
        // Postgres y Redis no publican puertos: el bloque de cada servicio no contiene "ports:".
        foreach (var servicio in new[] { "postgres", "redis" })
        {
            var inicio = compose.IndexOf($"\n  {servicio}:", StringComparison.Ordinal);
            var fin = compose.IndexOf("\n\n", inicio, StringComparison.Ordinal);
            Assert.DoesNotContain("ports:", compose[inicio..fin]);
        }
        // Observabilidad solo en loopback.
        Assert.Contains("127.0.0.1:9090:9090", compose);
        Assert.Contains("127.0.0.1:3000:3000", compose);
        // /metrics por un puerto interno que no se publica.
        Assert.Contains("Seguridad__Metricas__Puerto", compose);
        Assert.DoesNotContain("9091:9091", compose);
        Assert.DoesNotContain("password=escudo", compose, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Metricas_solo_por_el_puerto_interno_cuando_esta_configurado()
    {
        using var f = new FabricaApiConfig(new() { ["Seguridad:Metricas:Puerto"] = "9091" });
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/metrics")).StatusCode); // el puerto público (no 9091) no sirve /metrics
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/v1/salud")).StatusCode);
        var texto = await (await c.GetAsync("/health/ready")).Content.ReadAsStringAsync();
        Assert.Equal("Healthy", texto); // sin detalle de dependencias, cadenas de conexión ni excepciones
    }

    [Fact]
    public async Task Cuerpos_enormes_json_profundo_y_mal_formado_se_rechazan_sin_500()
    {
        using var f = new FabricaApi();
        var c = f.CreateClient();
        // > 1 MiB
        var grande = new StringContent("{\"llave\":\"" + new string('a', 2_000_000) + "\"}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await c.PostAsync("/v1/auth/login", grande)).StatusCode);
        // JSON anidado a 5.000 niveles
        var profundo = new StringContent(new string('[', 5_000) + new string(']', 5_000), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/v1/auth/login", profundo)).StatusCode);
        // JSON truncado y tipos erróneos
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/v1/auth/login", new StringContent("{\"llave\":", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/v1/auth/login", new StringContent("{\"llave\":123}", Encoding.UTF8, "application/json"))).StatusCode);
    }
}

// ------------------------------------------------------------------------------------------------------ rate limiting
public class LimitadorTests
{
    [Fact]
    public async Task X_Forwarded_For_no_evade_el_limite_por_IP_sin_proxies_configurados()
    {
        using var f = new FabricaApiConfig(new() { ["Seguridad:Login:MaxIntentosPorIp"] = "3" });
        var estados = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/login")
            {
                Content = JsonContent.Create(new { llave = "llave-" + i, clave = "x", id_dispositivo = "D", departamento_ip = "Antioquia" }),
            };
            req.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}"); // IP distinta en cada intento
            estados.Add((await f.CreateClient().SendAsync(req)).StatusCode);
        }
        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], estados);
    }

    private static async Task<List<HttpStatusCode>> LoginsConXffAsync(FabricaApiConfig f, int n)
    {
        var estados = new List<HttpStatusCode>();
        for (var i = 0; i < n; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/login")
            {
                Content = JsonContent.Create(new { llave = "llave-" + i, clave = "x", id_dispositivo = "D", departamento_ip = "Antioquia" }),
            };
            req.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}");
            estados.Add((await f.CreateClient().SendAsync(req)).StatusCode);
        }
        return estados;
    }

    [Fact]
    public async Task X_Forwarded_For_de_un_origen_que_no_es_proxy_de_confianza_tambien_se_ignora()
    {
        using var f = new FabricaApiConfig(new() { ["Seguridad:Login:MaxIntentosPorIp"] = "2", ["Seguridad:ProxiesConfiables:0"] = "10.255.255.1", ["Seguridad:ProxiesConfiables:1"] = "10.9.0.0/16" },
            ipRemota: "192.0.2.50");
        var estados = await LoginsConXffAsync(f, 4);
        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], estados);
    }

    [Fact]
    public async Task Un_proxy_de_confianza_configurado_si_transmite_la_IP_real_del_cliente()
    {
        using var f = new FabricaApiConfig(new() { ["Seguridad:Login:MaxIntentosPorIp"] = "2", ["Seguridad:ProxiesConfiables:0"] = "10.9.0.0/16" }, ipRemota: "10.9.1.1");
        var estados = await LoginsConXffAsync(f, 4);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, estados); // cada cliente real (IP distinta tras el proxy) cuenta por separado
    }

    [Fact]
    public void El_limite_por_llave_bloquea_tras_los_intentos_y_se_libera_con_la_ventana()
    {
        var reloj = new RelojFalso(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var l = new LimitadorVentana(reloj);
        var v = TimeSpan.FromMinutes(5);
        for (var i = 0; i < 5; i++) Assert.True(l.Intentar("k", 5, v, out _));
        Assert.False(l.Intentar("k", 5, v, out var espera));
        Assert.InRange(espera.TotalSeconds, 1, 300);
        reloj.Avanzar(TimeSpan.FromMinutes(5.1));
        Assert.True(l.Intentar("k", 5, v, out _));
    }

    [Fact]
    public void La_tabla_esta_acotada_y_falla_cerrada_pero_se_recupera_al_vencer_las_ventanas()
    {
        var reloj = new RelojFalso(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var l = new LimitadorVentana(reloj, maxClaves: 50);
        var v = TimeSpan.FromMinutes(5);
        for (var i = 0; i < 50; i++) Assert.True(l.Intentar("k" + i, 5, v, out _));
        Assert.False(l.Intentar("nueva", 5, v, out _));   // llaves arbitrarias no pueden agrandar la tabla sin límite
        Assert.True(l.Intentar("k0", 5, v, out _));       // las llaves ya presentes siguen contando con normalidad
        reloj.Avanzar(TimeSpan.FromMinutes(6));
        Assert.True(l.Intentar("nueva", 5, v, out _));    // purga por vencimiento: no hay bloqueo permanente
    }
}

// -------------------------------------------------------------------------------------------------- validación de entrada
public class ValidacionEntradaTests(FabricaApi api) : IClassFixture<FabricaApi>
{
    private static HttpContent Json(object o) => JsonContent.Create(o, options: FabricaApi.Snake);

    [Fact]
    public async Task Montos_negativos_cero_o_enormes_y_fechas_absurdas_se_rechazan_con_400()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-VAL", Roles.Servicio);
        var ok = FabricaApi.Tx("C-VAL");
        foreach (var malo in new[]
                 {
                     ok with { MontoCop = -1 }, ok with { MontoCop = 0 }, ok with { MontoCop = long.MaxValue }, ok with { MontoCop = 1_000_000_000_001 },
                     ok with { SaldoAntesCop = -5 }, ok with { SaldoAntesCop = long.MaxValue }, ok with { AntiguedadCuentaDias = int.MaxValue },
                     ok with { FechaHora = "2999-01-01 00:00:00" }, ok with { FechaHora = "1900-01-01 00:00:00" }, ok with { FechaHora = "ayer" },
                     ok with { IdTransaccion = new string('x', 33) }, ok with { IdDestino = new string('d', 65) }, ok with { TipoOperacion = "X" },
                     ok with { Canal = "telepatia" }, ok with { DestinoNuevo = 2 }, ok with { IdCliente = "" }, ok with { DepartamentoIp = new string('z', 65) },
                 })
        {
            var resp = await servicio.PostAsync("/v1/score/transaccion", Json(malo));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await servicio.PostAsync("/v1/score/transaccion", Json(ok with { MontoCop = 1_000_000_000_000 }))).StatusCode);
    }

    [Fact]
    public async Task Una_transaccion_rechazada_no_toca_el_estado_en_linea()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-VAL2", Roles.Servicio);
        var estado = api.Services.GetRequiredService<IEstadoEnLinea>();
        var malo = FabricaApi.Tx("C-VAL-ESTADO") with { MontoCop = long.MaxValue, IdDestino = "DEST-VAL-ESTADO" };
        Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsync("/v1/score/transaccion", Json(malo))).StatusCode);
        var vista = (IVistaEstado)estado;
        Assert.Equal(0, vista.Historial("C-VAL-ESTADO").N);
        Assert.Equal(0, vista.NTxDestino7d("DEST-VAL-ESTADO"));
    }

    [Fact]
    public async Task Eventos_con_fecha_futura_o_ilegible_no_envenenan_el_estado_de_otra_cuenta()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-EV", Roles.Servicio);
        object Ev(string fecha, string id = "E-1") => new { id_evento = id, fecha_hora = fecha, id_cliente = "C-VICTIMA", tipo_evento = "cambio_sim_detectado", resultado = "exitoso" };

        foreach (var fecha in new[] { "2999-12-31 23:59:59", "1999-01-01 00:00:00", "no-es-fecha" })
            Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsync("/v1/eventos", Json(new { eventos = new[] { Ev(fecha) } }))).StatusCode);

        // Nada se aplicó: ni siquiera los eventos válidos del mismo lote.
        var lote = new { eventos = new[] { Ev("2026-07-15 13:00:00", "E-OK"), Ev("2999-12-31 23:59:59", "E-MALO") } };
        Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsync("/v1/eventos", Json(lote))).StatusCode);
        var ctx = api.Services.GetRequiredService<IEstadoEnLinea>().ConsultarLogin("C-VICTIMA", Tiempo.AEpochColombia(DateTime.UtcNow), "D", null);
        Assert.False(ctx.CambioSim48h);
    }

    [Fact]
    public async Task Eventos_nulos_demasiados_o_con_campos_enormes_son_400_nunca_500()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-EV2", Roles.Servicio);
        var nulo = new StringContent("{\"eventos\":[null]}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsync("/v1/eventos", nulo)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsync("/v1/eventos", new StringContent("{\"eventos\":null}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsync("/v1/eventos", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);

        var muchos = Enumerable.Range(0, 1001).Select(i => new { id_evento = "E" + i, fecha_hora = "2026-07-15 13:00:00", id_cliente = "C-M", tipo_evento = "cambio_clave" });
        Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsync("/v1/eventos", Json(new { eventos = muchos }))).StatusCode);

        var largo = new { eventos = new[] { new { id_evento = new string('e', 65), fecha_hora = "2026-07-15 13:00:00", id_cliente = "C-M", tipo_evento = "cambio_clave" } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsync("/v1/eventos", Json(largo))).StatusCode);
        var so = new { eventos = new[] { new { id_evento = "E1", fecha_hora = "2026-07-15 13:00:00", id_cliente = "C-M", tipo_evento = "registro_dispositivo", sistema_operativo = new string('s', 200) } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await servicio.PostAsync("/v1/eventos", Json(so))).StatusCode);
    }

    [Fact]
    public async Task La_ingesta_de_eventos_queda_auditada_sin_ids_de_clientes()
    {
        var servicio = await api.ClienteAutenticadoAsync("SVC-EV3", Roles.Servicio);
        var ev = new { eventos = new[] { new { id_evento = "E-AUD", fecha_hora = "2026-07-15 13:00:00", id_cliente = "C-AUDITADO-9", tipo_evento = "cambio_clave" } } };
        Assert.Equal(HttpStatusCode.OK, (await servicio.PostAsync("/v1/eventos", Json(ev))).StatusCode);
        using var scope = api.Services.CreateScope();
        var reg = await scope.ServiceProvider.GetRequiredService<EscudoDbContext>().Auditoria.AsNoTracking().Where(r => r.Evento == "eventos_ingeridos" && r.Actor == "SVC-EV3").ToListAsync();
        Assert.Single(reg);
        Assert.DoesNotContain("C-AUDITADO-9", reg[0].Detalle);
        Assert.True((await scope.ServiceProvider.GetRequiredService<ServicioAuditoria>().VerificarAsync()).Valida);
    }

    [Fact]
    public async Task Registro_de_dispositivos_esta_acotado_y_valida_el_formato()
    {
        var llave = await api.SembrarAsync("C-DISP-CAP");
        var token = (await (await api.LoginAsync(llave)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var c = api.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/v1/auth/dispositivos", Json(new { id_dispositivo = "<script>alert(1)</script>" }))).StatusCode);
        for (var i = 0; i < ManejadorRegistrarDispositivo.MaxDispositivosPorCliente - 1; i++) // ya existe DISP-1
            Assert.Equal(HttpStatusCode.Created, (await c.PostAsync("/v1/auth/dispositivos", Json(new { id_dispositivo = "D-" + i, sistema_operativo = "iOS" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync("/v1/auth/dispositivos", Json(new { id_dispositivo = "D-EXTRA" }))).StatusCode);
    }
}

// ------------------------------------------------------------------------------------------------ integridad de negocio
public class IntegridadNegocioTests(FabricaApi api) : IClassFixture<FabricaApi>
{
    [Fact]
    public async Task Dos_analistas_no_pueden_resolver_la_misma_alerta_dos_veces_token_de_concurrencia()
    {
        var id = Guid.NewGuid();
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EscudoDbContext>();
            db.Alertas.Add(new Alerta { Id = id, IdTransaccion = "T-CONC-" + id.ToString("N")[..8], IdCliente = "C-CONC", MontoCop = 1, Decision = "retener", CreadaUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        using var s1 = api.Services.CreateScope();
        using var s2 = api.Services.CreateScope();
        var db1 = s1.ServiceProvider.GetRequiredService<EscudoDbContext>();
        var db2 = s2.ServiceProvider.GetRequiredService<EscudoDbContext>();
        var a1 = await db1.Alertas.FirstAsync(a => a.Id == id);
        var a2 = await db2.Alertas.FirstAsync(a => a.Id == id);
        a1.Estado = EstadosAlerta.Resuelta; a1.Resolucion = "fraude_confirmado";
        a2.Estado = EstadosAlerta.Resuelta; a2.Resolucion = "falso_positivo";
        await db1.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public void El_bloqueo_definitivo_solo_se_activa_por_el_metodo_dedicado()
    {
        var setter = typeof(ReputacionCuenta).GetProperty(nameof(ReputacionCuenta.BloqueoDefinitivo))!.SetMethod!;
        Assert.True(setter.IsPrivate); // ningún otro componente puede asignarlo directamente
        var rep = new ReputacionCuenta { IdCuenta = "X" };
        rep.ActualizarAutomatica(0, ["mula"], DateTime.UtcNow);
        rep.AjustarPuntaje(30, DateTime.UtcNow);
        Assert.False(rep.BloqueoDefinitivo);
    }

    [Fact]
    public void Los_atributos_sensibles_no_entran_al_modelo_ni_a_la_politica()
    {
        var ruta = Path.Combine(AppContext.BaseDirectory, "artefactos", "atributos.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(ruta));
        var nombres = doc.RootElement.GetProperty("atributos").EnumerateArray().Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetProperty("nombre").GetString()!).ToList();
        Assert.NotEmpty(nombres);
        Assert.Empty(nombres.Intersect(EscudoYuma.Api.Shared.Scoring.CatalogoAtributos.Prohibidos));
        Assert.Equal(EscudoYuma.Api.Shared.Scoring.CatalogoAtributos.Nombres, nombres); // el modelo desplegado == el catálogo auditado
        foreach (var n in nombres)
        {
            Assert.DoesNotContain("rango_edad", n);
            Assert.DoesNotContain("tipo_cliente", n);
            Assert.False(n.Contains("departamento") && n != "ip_departamento_distinto", n); // solo la señal de IP, no el departamento del cliente
        }
        // La política solo recibe señales de transacción/sesión: su contexto no tiene campos demográficos.
        var campos = typeof(EscudoYuma.Api.Shared.Scoring.ContextoPolitica).GetProperties().Select(p => p.Name.ToLowerInvariant());
        Assert.DoesNotContain(campos, c => c.Contains("depart") || c.Contains("edad") || c.Contains("tipocliente"));
    }
}
