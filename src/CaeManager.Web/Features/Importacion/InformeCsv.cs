using System.Text;

namespace CaeManager.Web.Features.Importacion;

/// <summary>
/// CSV de los archivos que descarga el Paso 1 del Importador (errores, valores previos e informe). Separador «;» (lo que
/// abre Excel en configuración regional española), todo entrecomillado y, contra la inyección de fórmulas (OWASP ASVS
/// V5.2.2 / CSV injection), una celda de texto que empiece por «=», «+», «-», «@», tabulador o retorno se escribe con un
/// apóstrofo delante: una celda del archivo del usuario nunca se evalúa como fórmula al abrir el informe.
/// </summary>
public static class InformeCsv
{
    private const char Separador = ';';

    public static string Construir(IEnumerable<string> cabecera, IEnumerable<IReadOnlyList<string?>> filas)
    {
        var texto = new StringBuilder();
        Escribir(texto, [.. cabecera]);
        foreach (var fila in filas)
            Escribir(texto, fila);
        return texto.ToString();
    }

    public static string Celda(string? valor)
    {
        var limpio = valor ?? string.Empty;
        if (limpio.Length > 0 && limpio[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            limpio = "'" + limpio;
        return "\"" + limpio.Replace("\"", "\"\"") + "\"";
    }

    private static void Escribir(StringBuilder texto, IReadOnlyList<string?> fila)
    {
        for (var i = 0; i < fila.Count; i++)
        {
            if (i > 0) texto.Append(Separador);
            texto.Append(Celda(fila[i]));
        }

        texto.Append("\r\n");
    }
}
