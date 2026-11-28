using System.Text.Json;

namespace EscudoYuma.Api.Shared.Scoring;

public enum NivelInterno { Aprobar, Advertir, Verificar, Retener, Rechazar }

/// <summary>
/// Umbrales de la política. Los de decisión salen de training/artefactos/umbrales.json (calibrados fuera de línea en
/// sep-oct 2026, ver training/politica.py); los de rechazo son fijos a priori. Los valores por defecto replican ese JSON.
/// </summary>
public sealed class UmbralesDecision
{
    /// <summary>p &gt;= Desafiar: autenticación adicional.</summary>
    public double Desafiar { get; set; } = 0.46139460802078247;
    /// <summary>p &gt;= Retener: el dinero no sale.</summary>
    public double Retener { get; set; } = 0.9772326350212097;
    /// <summary>Rechazar si p &gt;= Retener Y fan-in del destino (7 d) &gt;= este valor Y reputación del destino &lt;= RechazoReputacionMax.</summary>
    public double RechazoFanInMin { get; set; } = 3;
    public double RechazoReputacionMax { get; set; } = 30;
    /// <summary>"Monto sobre saldo alto" para distinguir advertir (ingeniería social) de verificar.</summary>
    public double MontoSobreSaldoAlto { get; set; } = 0.8;
    /// <summary>Escalador de ingreso: score de ingreso &gt;= este valor y p &gt;= EscaladorPuntajeMin sube un "aprobar" a "verificar".</summary>
    public double EscaladorScoreIngreso { get; set; } = 0.4;
    public double EscaladorPuntajeMin { get; set; } = 0.2;
    /// <summary>
    /// Holgura numérica al comparar contra los umbrales. Los umbrales se eligieron sobre puntajes float32 de XGBoost (un umbral ES el puntaje
    /// de un fraude de validación) y ONNX Runtime difiere de XGBoost hasta ~1e-6; sin holgura ese caso borde caería por debajo del umbral.
    /// </summary>
    public double Tolerancia { get; set; } = 1e-6;
    // ---- Piso de reglas de seguridad (parámetros FIJOS a priori; port de training/politica.py::piso_reglas)
    /// <summary>"Vaciado": monto &gt;= 90 % del saldo.</summary>
    public double PisoSobreSaldo { get; set; } = 0.9;
    /// <summary>Holgura al comparar monto_sobre_saldo (la matriz de Python es float32: 0,9 -&gt; 0,89999998).</summary>
    public double PisoTolerancia { get; set; } = 1e-6;
    public double PisoMontoVaciadoMin { get; set; } = 100_000;
    public double PisoReputacionMax { get; set; } = 30;
    public double PisoFanInMin { get; set; } = 3;
    /// <summary>Minutos durante los que un score de ingreso sigue siendo "el de la sesión actual".</summary>
    public int VigenciaScoreIngresoMin { get; set; } = 60;

    public static UmbralesDecision Cargar(string rutaUmbralesJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(rutaUmbralesJson));
        var r = doc.RootElement;
        var u = new UmbralesDecision
        {
            Desafiar = r.GetProperty("umbral_desafiar").GetDouble(),
            Retener = r.GetProperty("umbral_retener").GetDouble(),
        };
        if (r.TryGetProperty("rechazo", out var rz))
        {
            u.RechazoFanInMin = rz.GetProperty("fanin_destino_7d_min").GetDouble();
            u.RechazoReputacionMax = rz.GetProperty("reputacion_destino_max").GetDouble();
        }
        return u;
    }
}

/// <summary>Señales que la política necesita, derivadas de los atributos y del estado de la sesión.</summary>
public readonly record struct ContextoPolitica(
    double FanInDestino, double ReputacionDestino, bool DestinoNuevo, double MontoSobreSaldo,
    bool SenalesDeCuenta, double? ScoreIngreso, bool CuentaBloqueada,
    bool DispositivoNuevo = false, bool CambioSimOClave = false, bool EsSalida = false, bool EsTransferencia = false, double MontoCop = 0,
    bool EsPagoQr = false)
{
    /// <param name="scoreIngreso">Score de ingreso vigente de la sesión (null si no hay). NO es atributo del modelo.</param>
    public static ContextoPolitica Desde(AtributosTransaccion a, double? scoreIngreso, bool cuentaBloqueada) => new(
        a["fanin_destino_7d"], a["reputacion_destino"], a["destino_nuevo"] == 1, a["monto_sobre_saldo"],
        // señales de toma de cuenta / SIM swapping: dispositivo, SIM, clave o intentos fallidos de ingreso
        a["dispositivo_nuevo"] == 1 || a["dispositivo_visto_antes"] == 0 || a["cambio_sim_48h"] == 1 || a["cambio_clave_24h"] == 1 || a["login_fallidos_24h"] > 0,
        scoreIngreso, cuentaBloqueada,
        // señales de la PROPIA transacción para el piso de reglas de seguridad (no dependen del feed de eventos)
        a["dispositivo_nuevo"] == 1, a["cambio_sim_48h"] == 1 || a["cambio_clave_24h"] == 1,
        a["op_transferencia_inmediata"] == 1 || a["op_retiro_corresponsal"] == 1 || a["op_retiro_cajero"] == 1,
        a["op_transferencia_inmediata"] == 1, a["monto_cop"], a["op_pago_qr"] == 1);
}

/// <summary>Regla de seguridad que elevó la decisión del modelo (Ninguno = el piso no cambió nada).</summary>
public enum PisoSeguridad { Ninguno, DispositivoSimClave, DispositivoSimClaveVaciado, Vaciado, DestinoReputacionBaja }

public readonly record struct ResultadoPolitica(NivelInterno Nivel, bool EscaladaPorIngreso, PisoSeguridad Piso = PisoSeguridad.Ninguno);

/// <summary>
/// Política de decisión (Singleton: sin estado mutable, umbrales fijos al arrancar). Port de training/politica.py::decidir:
///   p &lt; desafiar -> aprobar ; desafiar &lt;= p &lt; retener -> desafiar ; p &gt;= retener -> retener ;
///   p &gt;= retener Y fan-in &gt;= 3 Y reputación destino &lt;= 30 -> rechazar.
/// Mapeo a 5 niveles internos (el sandbox solo conoce 4 decisiones): "desafiar" se parte en advertir / verificar.
/// Cuenta con bloqueo definitivo (decidido por un analista) -> rechazar.
/// </summary>
public sealed class PoliticaDecision(UmbralesDecision umbrales)
{
    public UmbralesDecision Umbrales { get; } = umbrales;

    /// <summary>
    /// Decisión final = max(decisión del modelo, piso de reglas de seguridad). El piso es independiente del modelo y de los eventos de la app:
    ///   A) dispositivo nuevo Y (cambio de SIM O de clave) Y salida                     -> al menos desafiar (verificar);
    ///      y además monto &gt;= 90 % del saldo Y destino nuevo                           -> al menos retener (a un analista; no bloquea la cuenta);
    ///   B) vaciado (transferencia, destino nuevo, monto &gt;= 90 % del saldo y &gt;= 100.000) -> al menos desafiar (advertir);
    ///   C) reputación del destino &lt;= 30 Y fan-in &gt;= 3                                -> al menos desafiar (verificar).
    /// Solo SUBE la decisión del modelo (se compara por decisión del contrato: aprobar &lt; desafiar &lt; retener &lt; rechazar).
    /// </summary>
    public ResultadoPolitica Decidir(double p, ContextoPolitica c)
    {
        var res = DecidirModelo(p, c);
        if (c.CuentaBloqueada) return res;
        var (nivelPiso, piso) = Piso(c);
        return piso != PisoSeguridad.Ninguno && Rango(nivelPiso) > Rango(res.Nivel) ? new ResultadoPolitica(nivelPiso, false, piso) : res;
    }

    private static int Rango(NivelInterno n) => n switch
    {
        NivelInterno.Aprobar => 0, NivelInterno.Advertir or NivelInterno.Verificar => 1, NivelInterno.Retener => 2, _ => 3,
    };

    /// <summary>Piso de reglas de seguridad. Prioridad (igual que politica.py): A-retener &gt; A &gt; B &gt; C.</summary>
    public (NivelInterno Nivel, PisoSeguridad Piso) Piso(ContextoPolitica c)
    {
        var u = Umbrales;
        var vaciadoANuevo = c.DestinoNuevo && c.MontoSobreSaldo >= u.PisoSobreSaldo - u.PisoTolerancia;
        var a = c.DispositivoNuevo && c.CambioSimOClave && c.EsSalida;
        if (a && vaciadoANuevo) return (NivelInterno.Retener, PisoSeguridad.DispositivoSimClaveVaciado);
        if (a) return (NivelInterno.Verificar, PisoSeguridad.DispositivoSimClave);
        if (c.EsTransferencia && vaciadoANuevo && c.MontoCop >= u.PisoMontoVaciadoMin) return (NivelInterno.Advertir, PisoSeguridad.Vaciado);
        if (c.ReputacionDestino <= u.PisoReputacionMax && c.FanInDestino >= u.PisoFanInMin) return (NivelInterno.Verificar, PisoSeguridad.DestinoReputacionBaja);
        return (NivelInterno.Aprobar, PisoSeguridad.Ninguno);
    }

    /// <summary>
    /// Revisión posterior: un "desafiar" SIN señales de toma de cuenta (dispositivo, SIM, clave, ingresos fallidos ni ingreso riesgoso) en una
    /// transferencia o pago QR abre un caso para el analista. La biometría confirma QUIÉN opera, no si lo están engañando: en ingeniería social
    /// y engaño QR la propia víctima la supera y el dinero sale. La decisión no cambia (la operación sigue su curso tras la verificación).
    /// Medido en validación sep-oct 2026: ~3 casos/día extra con 36 % de precisión; cubre 48/79 de ingeniería social y 17/40 de engaño QR.
    /// </summary>
    public static bool AbreRevisionPosterior(ResultadoPolitica res, ContextoPolitica c) =>
        res.Nivel is NivelInterno.Advertir or NivelInterno.Verificar && !res.EscaladaPorIngreso && !c.SenalesDeCuenta && !c.CuentaBloqueada
        && (c.EsTransferencia || c.EsPagoQr);

    public const string MotivoRevisionPosterior =
        "Revisión posterior: el patrón es de estafa con la propia víctima operando (la verificación biométrica no lo evita); confirme con el cliente.";

    /// <summary>Texto de la razón cuando el piso eleva la decisión (comienza con "Regla de seguridad:").</summary>
    public static string MotivoPiso(PisoSeguridad piso) => piso switch
    {
        PisoSeguridad.DispositivoSimClave => "Regla de seguridad: dispositivo nuevo junto con cambio de SIM o de clave recientes en una operación de salida.",
        PisoSeguridad.DispositivoSimClaveVaciado => "Regla de seguridad: dispositivo nuevo con cambio de SIM o de clave y casi todo el saldo hacia un destino nuevo; pasa a un analista.",
        PisoSeguridad.Vaciado => "Regla de seguridad: transferencia que vacía el saldo hacia un destino nuevo (posible ingeniería social).",
        PisoSeguridad.DestinoReputacionBaja => "Regla de seguridad: el destino recibe de muchos remitentes distintos y tiene reputación baja.",
        _ => "",
    };

    private ResultadoPolitica DecidirModelo(double p, ContextoPolitica c)
    {
        if (c.CuentaBloqueada) return new(NivelInterno.Rechazar, false);
        if (p >= Umbrales.Retener - Umbrales.Tolerancia)
            return new(c.FanInDestino >= Umbrales.RechazoFanInMin && c.ReputacionDestino <= Umbrales.RechazoReputacionMax
                ? NivelInterno.Rechazar : NivelInterno.Retener, false);
        if (p >= Umbrales.Desafiar - Umbrales.Tolerancia)
        {
            // Señales de dispositivo/SIM/clave/login -> verificar (toma de cuenta). Sin ellas, destino nuevo + monto que
            // consume casi todo el saldo -> advertir (patrón de ingeniería social: el cliente mismo hace la operación).
            if (c.SenalesDeCuenta) return new(NivelInterno.Verificar, false);
            return new(c.DestinoNuevo && c.MontoSobreSaldo >= Umbrales.MontoSobreSaldoAlto ? NivelInterno.Advertir : NivelInterno.Verificar, false);
        }
        // Escalador: un ingreso riesgoso en la sesión sube un "aprobar" con algo de riesgo a verificar. Solo escala, nunca baja.
        if (c.ScoreIngreso is { } s && s >= Umbrales.EscaladorScoreIngreso && p >= Umbrales.EscaladorPuntajeMin)
            return new(NivelInterno.Verificar, true);
        return new(NivelInterno.Aprobar, false);
    }

    /// <summary>5 niveles internos -> 4 decisiones del contrato del sandbox.</summary>
    public static string Mapear(NivelInterno nivel) => nivel switch
    {
        NivelInterno.Aprobar => "aprobar",
        NivelInterno.Advertir => "desafiar",
        NivelInterno.Verificar => "desafiar",
        NivelInterno.Retener => "retener",
        NivelInterno.Rechazar => "rechazar",
        _ => throw new ArgumentOutOfRangeException(nameof(nivel)),
    };

    public static string NombreInterno(NivelInterno nivel) => nivel.ToString().ToLowerInvariant();
}
