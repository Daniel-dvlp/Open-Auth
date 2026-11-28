using System.Globalization;
using System.Text;

namespace EscudoYuma.Api.Shared.Scoring;

/// <summary>
/// Ubicación de la conexión. Yuma Pay solo opera en Colombia: una conexión cuya ubicación no es uno de los 33 departamentos
/// (32 + Bogotá D.C.) se trata como "fuera de Colombia". Compara sin tildes ni mayúsculas. Vacío = desconocida (no se marca).
/// No es atributo del modelo (los datos del caso solo traen departamentos colombianos): la usan el score de ingreso y el piso de seguridad.
/// </summary>
public static class UbicacionIp
{
    private static readonly HashSet<string> Departamentos = new[]
    {
        "Amazonas", "Antioquia", "Arauca", "Atlántico", "Bogotá D.C.", "Bolívar", "Boyacá", "Caldas", "Caquetá", "Casanare", "Cauca",
        "Cesar", "Chocó", "Córdoba", "Cundinamarca", "Guainía", "Guaviare", "Huila", "La Guajira", "Magdalena", "Meta", "Nariño",
        "Norte de Santander", "Putumayo", "Quindío", "Risaralda", "San Andrés y Providencia", "Santander", "Sucre", "Tolima",
        "Valle del Cauca", "Vaupés", "Vichada",
    }.Select(Normalizar).ToHashSet();

    public static bool EsFueraDeColombia(string? departamentoIp) =>
        !string.IsNullOrWhiteSpace(departamentoIp) && !Departamentos.Contains(Normalizar(departamentoIp));

    private static string Normalizar(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (var ch in texto.Trim().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
