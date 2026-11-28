using EscudoYuma.Api.Shared.Persistencia;

namespace EscudoYuma.Api.Shared.Auditoria;

/// <summary>Bitácora encadenada del Escudo (implementación común en <see cref="ServicioAuditoriaBase{TDb}"/>).</summary>
public sealed class ServicioAuditoria(EscudoDbContext db, ContextoAuditoria ctx, IHttpContextAccessor http, TimeProvider reloj, EstadoCadenaAuditoria cadena,
    IServiceScopeFactory fabrica, ILogger<ServicioAuditoria> log)
    : ServicioAuditoriaBase<EscudoDbContext>(db, ctx, http, reloj, cadena, fabrica, log);
