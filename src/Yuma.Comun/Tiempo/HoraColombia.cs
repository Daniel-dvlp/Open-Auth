using System.Globalization;

namespace Yuma.Comun.Tiempo;

/// <summary>
/// Hora legal de Colombia: UTC-5 todo el año (sin horario de verano). Los datos del caso (`fecha_hora`) y el contrato del Escudo
/// usan hora local de Colombia con formato <c>AAAA-MM-DD HH:MM:SS</c>; internamente todo se guarda en UTC.
/// </summary>
public static class HoraColombia
{
    public const string Formato = "yyyy-MM-dd HH:mm:ss";
    public static readonly TimeSpan Desfase = TimeSpan.FromHours(-5);

    public static DateTime DesdeUtc(DateTime utc) => DateTime.SpecifyKind(utc + Desfase, DateTimeKind.Unspecified);

    public static string Formatear(DateTime utc) => DesdeUtc(utc).ToString(Formato, CultureInfo.InvariantCulture);

    /// <summary>Convierte un `fecha_hora` local de Colombia a UTC.</summary>
    public static DateTime AUtc(string fechaLocal) =>
        DateTime.SpecifyKind(DateTime.ParseExact(fechaLocal, Formato, CultureInfo.InvariantCulture) - Desfase, DateTimeKind.Utc);
}
