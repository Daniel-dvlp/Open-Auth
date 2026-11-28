using System.Diagnostics;
using System.Globalization;
using EscudoYuma.Api.Shared.Auditoria;
using EscudoYuma.Api.Shared.Estado;
using EscudoYuma.Api.Shared.Expediente;
using EscudoYuma.Api.Shared.Observabilidad;
using EscudoYuma.Api.Shared.Persistencia;
using EscudoYuma.Api.Shared.ReputacionCuentas;
using EscudoYuma.Api.Shared.Reglas;
using EscudoYuma.Api.Shared.Scoring;
using EscudoYuma.Api.Shared.Seguridad;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace EscudoYuma.Api.Features.Scoring.EvaluarTransaccion;

// ---------- Contrato (la entrada es el esquema Transaccion del openapi del sandbox) ----------
public sealed record EvaluarTransaccionResponse(
    string Decision, string DecisionInterna, double Puntaje, IReadOnlyList<string> Razones, double LatenciaMs, string VersionModelo, double LatenciaTotalMs);

public sealed class TransaccionValidator : AbstractValidator<Transaccion>
{
    private static readonly string[] Canales = ["app", "corresponsal", "cajero"];
    private static readonly string[] TiposDestino = ["cuenta_yuma", "otra_entidad", "comercio", "corresponsal", "cajero", "servicio", "operador"];

    /// <summary>Tope por monto: 1 billón de COP. Evita desbordar las sumas long de las ventanas del estado en línea (los datos reales llegan a ~9 M).</summary>
    public const long MaxMontoCop = 1_000_000_000_000;
    public const int MaxAntiguedadDias = 36_500;

    public TransaccionValidator(TimeProvider reloj)
    {
        RuleFor(x => x.IdTransaccion).NotEmpty().MaximumLength(32);
        RuleFor(x => x.IdCliente).NotEmpty().MaximumLength(32);
        RuleFor(x => x.FechaHora).Must(f => DateTime.TryParseExact(f, Transaccion.FormatoFecha, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .WithMessage("fecha_hora debe tener el formato AAAA-MM-DD HH:MM:SS.");
        // Una fecha lejana en el futuro (o absurda) envenenaría las ventanas del estado en línea de ese cliente y del destino.
        RuleFor(x => x.FechaHora).Must(f => RangoFecha.EsValida(f, reloj.GetUtcNow().UtcDateTime))
            .When(x => DateTime.TryParseExact(x.FechaHora, Transaccion.FormatoFecha, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .WithMessage("fecha_hora fuera de rango (no antes de 2020 ni más de 1 día en el futuro).");
        RuleFor(x => x.TipoOperacion).Must(t => Transaccion.Tipos.Contains(t)).WithMessage("tipo_operacion no reconocido.");
        RuleFor(x => x.Canal).Must(c => Canales.Contains(c)).WithMessage("canal debe ser app, corresponsal o cajero.");
        RuleFor(x => x.TipoDestino).Must(c => TiposDestino.Contains(c)).WithMessage("tipo_destino no reconocido.");
        RuleFor(x => x.MontoCop).GreaterThan(0).LessThanOrEqualTo(MaxMontoCop);
        RuleFor(x => x.SaldoAntesCop).GreaterThanOrEqualTo(0).LessThanOrEqualTo(MaxMontoCop);
        RuleFor(x => x.AntiguedadCuentaDias).InclusiveBetween(0, MaxAntiguedadDias);
        RuleFor(x => x.IdDestino).NotEmpty().MaximumLength(64);
        RuleFor(x => x.IdDispositivo).NotEmpty().MaximumLength(64);
        RuleFor(x => x.DepartamentoIp).MaximumLength(64);
        foreach (var (nombre, sel) in new (string, Func<Transaccion, int>)[]
                 {
                     ("destino_nuevo", t => t.DestinoNuevo), ("dispositivo_nuevo", t => t.DispositivoNuevo),
                     ("cambio_clave_24h", t => t.CambioClave24h), ("cambio_sim_48h", t => t.CambioSim48h),
                     ("ip_departamento_distinto", t => t.IpDepartamentoDistinto),
                 })
            RuleFor(x => sel(x)).InclusiveBetween(0, 1).WithMessage($"{nombre} debe ser 0 o 1.");
    }
}

// ---------- Pipeline ----------
/// <summary>
/// Pipeline: IEstadoEnLinea (atributos + actualización atómica) -> IModeloRiesgo (ONNX) -> PoliticaDecision -> razones ->
/// reputación (cola asíncrona) -> auditoría -> alerta. Scoped: usa el DbContext y la auditoría del request; los
/// colaboradores costosos (estado, modelo, política, explicador) son Singleton.
/// <c>LatenciaMs</c> mide solo el scoring interno (estado, atributos, modelo, política y razones); <c>LatenciaTotalMs</c> suma auditoría y alerta.
/// </summary>
public sealed class ManejadorEvaluarTransaccion(
    EscudoDbContext db, IEstadoEnLinea estado, IModeloRiesgoFactory fabrica, PoliticaDecision politica,
    ExplicadorRazones explicador, RegistroBloqueos bloqueos, ColaReputacion reputaciones, ServicioAuditoria auditoria, TimeProvider reloj,
    RegistroExpediente expediente, ColaNarrativas narrativas, ContextoAuditoria contexto, IHttpContextAccessor http)
{
    public async Task<EvaluarTransaccionResponse> EjecutarAsync(Transaccion tx, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;

        // 1) Estado en línea -> atributos (solo estado previo) y actualización atómica con esta transacción
        var r = estado.ProcesarTx(tx);
        var attrs = r.Atributos;

        // 2) Modelo ONNX: probabilidad de fraude
        var modelo = fabrica.Crear(TipoModelo.Transaccion);
        var p = modelo.Predecir(attrs);

        // 3) Política (umbrales.json). Una cuenta con bloqueo definitivo (decidido por un analista) siempre se rechaza;
        //    este pipeline NUNCA crea ese bloqueo. El score de ingreso de la sesión NO es atributo del modelo: solo escala la política.
        var bloqueada = bloqueos.Contiene(tx.IdCliente);
        var scoreIngreso = estado.ScoreIngresoVigente(tx.IdCliente, ahoraUtc, TimeSpan.FromMinutes(politica.Umbrales.VigenciaScoreIngresoMin));
        var ctxPolitica = ContextoPolitica.Desde(attrs, scoreIngreso, bloqueada);
        var res = politica.Decidir(p, ctxPolitica);
        var nivel = res.Nivel;
        var revisionPosterior = PoliticaDecision.AbreRevisionPosterior(res, ctxPolitica);
        var decision = PoliticaDecision.Mapear(nivel);

        // 4) Razones legibles (nunca vacías)
        var razones = Razones(attrs, p, res, bloqueada, scoreIngreso);
        var msScoring = total.Elapsed.TotalMilliseconds;

        // 5) Reputación de origen y destino: persistencia asíncrona, no bloquea la respuesta
        reputaciones.Encolar(r.Origen, ahoraUtc);
        if (r.Destino is { } d) reputaciones.Encolar(d, ahoraUtc);

        // 6) Auditoría y alerta (solo retener/rechazar)
        var eslabon = await auditoria.RegistrarAsync("transaccion_evaluada", "score/transaccion", new
        {
            id_transaccion = tx.IdTransaccion, id_cliente = tx.IdCliente, decision, decision_interna = PoliticaDecision.NombreInterno(nivel),
            puntaje = Math.Round(p, 6), reglas = attrs.Reglas.Select(x => x.IdRegla), version_modelo = modelo.Version,
        }, ct: ct);

        // Expediente (asíncrono, no suma latencia): la operación y la decisión con sus normas, enlazadas al eslabón de auditoría.
        var conExpediente = contexto.Actor != "replay-sandbox"; // el replay masivo no alimenta expedientes ni narrativas
        var fechaNegocioUtc = DateTime.SpecifyKind(tx.FechaLocal.AddHours(5), DateTimeKind.Utc); // hora de la operación (Colombia -> UTC)
        if (conExpediente)
        {
            var fuente = http.HttpContext?.User.FindFirst("rol")?.Value == Roles.Servicio ? FuentesHecho.Billetera : FuentesHecho.Escudo;
            expediente.Registrar(TiposHecho.Transaccion, fuente, tx.IdCliente, tx.IdTransaccion, new
            {
                fecha_hora = tx.FechaHora, tipo_operacion = tx.TipoOperacion, canal = tx.Canal, monto_cop = tx.MontoCop, saldo_antes_cop = tx.SaldoAntesCop,
                id_destino = tx.IdDestino, tipo_destino = tx.TipoDestino, destino_nuevo = tx.DestinoNuevo, id_dispositivo = tx.IdDispositivo,
                dispositivo_nuevo = tx.DispositivoNuevo, cambio_clave_24h = tx.CambioClave24h, cambio_sim_48h = tx.CambioSim48h,
                departamento_ip = tx.DepartamentoIp, ip_departamento_distinto = tx.IpDepartamentoDistinto, antiguedad_cuenta_dias = tx.AntiguedadCuentaDias,
            }, null, contexto.IdCorrelacion, eslabon, fechaNegocioUtc);
            var normas = attrs.Reglas.Select(x => x.IdRegla).Append("POL-UMBRALES").Append(CatalogoNormas.Piso(res.Piso)).ToList();
            if (res.EscaladaPorIngreso) normas.Add("ESC-INGRESO");
            if (decision == "desafiar") normas.Add("NORMA-AUTENTICACION-REFORZADA");
            if (bloqueada) normas.Add("NORMA-BLOQUEO-ANALISTA");
            if (revisionPosterior) normas.Add("NORMA-REVISION-POSTERIOR");
            expediente.Registrar(TiposHecho.Decision, FuentesHecho.Escudo, tx.IdCliente, tx.IdTransaccion, new
            {
                decision, decision_interna = PoliticaDecision.NombreInterno(nivel), puntaje = Math.Round(p, 6), version_modelo = modelo.Version,
                reglas_disparadas = attrs.Reglas.Select(x => new { id = x.IdRegla, severidad = x.Severidad, razon = x.Razon }),
                razones, score_ingreso = scoreIngreso, escalada_por_ingreso = res.EscaladaPorIngreso, cuenta_bloqueada = bloqueada,
                latencia_scoring_ms = Math.Round(msScoring, 3),
            }, normas, contexto.IdCorrelacion, eslabon, fechaNegocioUtc);
        }

        // Alerta: retener/rechazar, o "desafiar" con patrón de estafa sin señales de toma de cuenta (revisión posterior; la decisión no cambia)
        if (nivel >= NivelInterno.Retener || revisionPosterior)
        {
            if (!await db.Alertas.AnyAsync(a => a.IdTransaccion == tx.IdTransaccion, ct))
            {
                var razonesAlerta = revisionPosterior ? razones.Prepend(PoliticaDecision.MotivoRevisionPosterior).ToList() : razones.ToList();
                var alerta = new Alerta
                {
                    IdTransaccion = tx.IdTransaccion, IdCliente = tx.IdCliente, MontoCop = tx.MontoCop, Puntaje = p,
                    Decision = decision, Razones = razonesAlerta, CreadaUtc = ahoraUtc,
                };
                db.Alertas.Add(alerta);
                await db.SaveChangesAsync(ct);
                var eslabonAlerta = await auditoria.RegistrarAsync("alerta_creada", "alertas",
                    new { id_alerta = alerta.Id, id_transaccion = tx.IdTransaccion, decision, origen = revisionPosterior ? "revision_posterior" : "decision" }, ct: ct);
                if (conExpediente)
                {
                    expediente.Registrar(TiposHecho.Alerta, FuentesHecho.Escudo, tx.IdCliente, tx.IdTransaccion,
                        new { id_alerta = alerta.Id, decision, monto_cop = tx.MontoCop }, null, contexto.IdCorrelacion, eslabonAlerta, ahoraUtc);
                    narrativas.Encolar(alerta.Id); // narrativa en segundo plano: nunca en el camino de la decisión
                }
            }
        }

        total.Stop();
        MetricasYuma.Decisiones.WithLabels(decision).Inc();
        MetricasYuma.DecisionesInternas.WithLabels(PoliticaDecision.NombreInterno(nivel)).Inc();
        MetricasYuma.LatenciaScoring.Observe(msScoring);
        return new EvaluarTransaccionResponse(decision, PoliticaDecision.NombreInterno(nivel), Math.Round(p, 6), razones,
            Math.Round(msScoring, 3), modelo.Version, Math.Round(total.Elapsed.TotalMilliseconds, 3));
    }

    private IReadOnlyList<string> Razones(AtributosTransaccion a, double p, ResultadoPolitica res, bool bloqueada, double? scoreIngreso)
    {
        if (bloqueada) return ["Cuenta con bloqueo definitivo decidido por un analista"];
        if (res.Nivel == NivelInterno.Aprobar) return [ExplicadorRazones.RazonGenerica(res.Nivel, p)];

        var razones = new List<string>(5);
        if (res.Piso != PisoSeguridad.Ninguno) razones.Add(PoliticaDecision.MotivoPiso(res.Piso)); // "Regla de seguridad: ..."
        if (res.Nivel == NivelInterno.Advertir)
            razones.Add("Advertencia: confirme con el beneficiario por otro canal antes de continuar; las estafas suelen pedir vaciar el saldo hacia una cuenta nueva.");
        if (res.EscaladaPorIngreso)
            razones.Add($"El ingreso a la sesión fue riesgoso (score {scoreIngreso:0.00}): se pide verificación adicional.");
        if (res.Nivel == NivelInterno.Rechazar)
            razones.Add($"El destino recibe de {a["fanin_destino_7d"]:0} remitentes distintos y su reputación es baja ({a["reputacion_destino"]:0}/100): operación rechazada.");
        razones.AddRange(explicador.Explicar(a));
        if (razones.Count == 0) razones.Add(ExplicadorRazones.RazonGenerica(res.Nivel, p));
        return razones;
    }
}

public sealed class EvaluarTransaccionFeature : IFeature
{
    public void AddServices(IServiceCollection s)
    {
        s.AddScoped<IValidator<Transaccion>, TransaccionValidator>();
        s.AddScoped<ManejadorEvaluarTransaccion>();

        // Singletons: recursos costosos o sin estado mutable (ver docs/patrones.md)
        s.AddSingleton(sp => ArtefactosOptions.Desde(sp.GetRequiredService<IConfiguration>()));
        foreach (var regla in CatalogoReglas.ReglasVigentes()) s.AddSingleton(regla);
        s.AddSingleton<CatalogoReglas>();
        s.AddSingleton(sp => ImputacionEventos.Cargar(sp.GetRequiredService<ArtefactosOptions>().Ruta("imputacion.json")));
        s.AddSingleton<IConstructorAtributos, ConstructorAtributos>();
        s.AddSingleton<IEstadoEnLinea, EstadoEnLinea>();
        s.AddSingleton<IModeloRiesgoFactory, ModeloRiesgoFactory>();
        s.AddSingleton(sp => new ExplicadorRazones(sp.GetRequiredService<ArtefactosOptions>().Ruta("explicacion.json")));
        s.AddSingleton(sp =>
        {
            // Umbrales calibrados fuera de línea (umbrales.json); la sección Scoring:Umbrales puede sobrescribirlos.
            var umbrales = UmbralesDecision.Cargar(sp.GetRequiredService<ArtefactosOptions>().Ruta("umbrales.json"));
            sp.GetRequiredService<IConfiguration>().GetSection("Scoring:Umbrales").Bind(umbrales);
            return new PoliticaDecision(umbrales);
        });
        s.AddSingleton<RegistroBloqueos>();
        s.AddSingleton<ColaReputacion>();
        s.AddHostedService<EscritorReputacion>();
        s.AddHostedService<PrecargaMotor>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r) =>
        r.MapPost("/v1/score/transaccion", async (Transaccion tx, ManejadorEvaluarTransaccion h, CancellationToken ct) =>
                Results.Ok(await h.EjecutarAsync(tx, ct)))
            .RequireAuthorization(Politicas.SistemaOAnalista).ValidarCon<Transaccion>().WithTags("Scoring");
}
