using System.Globalization;
using System.Text;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Nombre canónico de un fichero de Documento al descargarlo, enviarlo o exportarlo.
/// Se CALCULA en cada salida: no se fija al subir y no toca la clave del blob
/// (opaca, un blob un propietario). Usa la fecha de EMISIÓN, nunca la de
/// vencimiento (la vigencia varía por Cliente empresarial). Nunca incluye
/// DNI/NIE: el propietario entra por su nombre, no por su identificador.
/// </summary>
public static class NombreArchivoDocumento
{
    public const int LongitudMaxima = 120;
    private const string AliasAptitudMedica = "Aptitud medica";

    /// <summary>Descarga suelta: <c>Apellidos Nombre - Tipo - emitido AAAA-MM-DD[_vN].pdf</c>.</summary>
    /// <param name="ordinalMismoDia">1 = único (sin sufijo); N ≥ 2 = <c>_vN</c> cuando coinciden propietario, tipo y fecha.</param>
    public static string Suelto(string? propietario, string? tipo, DateOnly emision, int ordinalMismoDia = 1, string extension = ".pdf")
    {
        var sufijo = $" - emitido {emision:yyyy-MM-dd}" + (ordinalMismoDia > 1 ? $"_v{ordinalMismoDia}" : "");
        return Componer(
            [Limpiar(propietario, "Sin nombre"), Limpiar(TipoCorto(tipo), "Documento")],
            " - ", sufijo, extension);
    }

    /// <summary>
    /// Fichero dentro de una carpeta de exportación: <c>NN-Tipo (AAAA-MM-DD)[_vN].pdf</c>,
    /// con NN = <c>TipoDocumento.Orden</c>.
    /// </summary>
    public static string Entrada(int orden, string? tipo, DateOnly emision, int ordinalMismoDia = 1, string extension = ".pdf")
    {
        var sufijo = $" ({emision:yyyy-MM-dd})" + (ordinalMismoDia > 1 ? $"_v{ordinalMismoDia}" : "");
        return Componer([$"{Math.Clamp(orden, 0, 99):00}-" + Limpiar(TipoCorto(tipo), "Documento")], "", sufijo, extension);
    }

    /// <summary>
    /// «Certificado de aptitud médica» y «Reconocimiento médico» son el mismo tipo para
    /// el propietario; el resto de tipos va con su nombre completo (la
    /// <c>Sensibilidad</c> del tipo queda como posible regla, decisión pendiente).
    /// </summary>
    public static string TipoCorto(string? tipo)
    {
        var limpio = Limpiar(tipo, "").ToLowerInvariant();
        return limpio is "certificado de aptitud medica" or "reconocimiento medico" or "aptitud medica"
            ? AliasAptitudMedica
            : tipo ?? "";
    }

    /// <summary>¿Dos nombres de tipo producen el mismo componente de fichero?</summary>
    public static bool MismoTipo(string? a, string? b) =>
        string.Equals(Limpiar(TipoCorto(a), "Documento"), Limpiar(TipoCorto(b), "Documento"), StringComparison.OrdinalIgnoreCase);

    /// <summary>Sin acentos ni ñ, solo letras, dígitos, espacio y <c>- . ( )</c>; espacios colapsados.</summary>
    public static string Limpiar(string? texto, string alternativa)
    {
        if (string.IsNullOrWhiteSpace(texto)) return alternativa;

        var sb = new StringBuilder();
        foreach (var c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.' or '(' or ')')
                sb.Append(c);
            else
                sb.Append(' ');
        }

        var resultado = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        return resultado.Length == 0 ? alternativa : resultado;
    }

    // El sufijo (fecha, versión, extensión) es información que no puede perderse: el recorte
    // se reparte sobre las partes variables, empezando por la última (el tipo identifica
    // menos que el propietario cuando ambos son largos, pero el propietario nunca queda vacío).
    private static string Componer(string[] partes, string separador, string sufijo, string extension)
    {
        var presupuesto = LongitudMaxima - sufijo.Length - extension.Length;
        var fijo = separador.Length * (partes.Length - 1);
        var disponible = Math.Max(partes.Length, presupuesto - fijo);

        var recortadas = (string[])partes.Clone();
        while (recortadas.Sum(p => p.Length) > disponible)
        {
            var i = Array.IndexOf(recortadas, recortadas.OrderByDescending(p => p.Length).First());
            recortadas[i] = recortadas[i][..^1].TrimEnd('.', ' ', '-');
            if (recortadas[i].Length == 0) recortadas[i] = "x";
            if (recortadas.All(p => p.Length <= 1)) break;
        }

        return string.Join(separador, recortadas) + sufijo + extension;
    }
}
