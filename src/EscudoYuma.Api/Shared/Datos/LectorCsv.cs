using System.Text;

namespace EscudoYuma.Api.Shared.Datos;

/// <summary>
/// Lector mínimo de los CSV sintéticos del caso de estudio (UTF-8 con BOM, separador coma, sin comillas ni saltos de línea
/// dentro de campos: verificado sobre 03_Datos). Devuelve cada fila como diccionario columna -> valor.
/// </summary>
public static class LectorCsv
{
    public static IEnumerable<Dictionary<string, string>> Leer(string ruta)
    {
        using var lector = new StreamReader(ruta, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var cabecera = lector.ReadLine() ?? throw new InvalidDataException($"CSV vacío: {ruta}");
        var columnas = cabecera.TrimStart('﻿').Split(',');
        while (lector.ReadLine() is { } linea)
        {
            if (linea.Length == 0) continue;
            var campos = linea.Split(',');
            var fila = new Dictionary<string, string>(columnas.Length);
            for (var i = 0; i < columnas.Length; i++) fila[columnas[i]] = i < campos.Length ? campos[i] : "";
            yield return fila;
        }
    }
}
