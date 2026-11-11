using System.Globalization;
using System.Text.RegularExpressions;

namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Máscara dd/mm/aaaa de <see cref="CampoFecha"/>. El navegador aplica la misma en <c>wwwroot/js/campo-fecha.js</c> para
/// que el cursor no espere al servidor; esta copia es la que vale cuando el script no ha corrido (bUnit, script bloqueado)
/// y la que decide qué texto es una fecha. Las dos tienen que dar el mismo resultado: si difirieran, el servidor
/// reescribiría en cada pulsación lo que el navegador acaba de formatear.
/// </summary>
public static partial class MascaraFecha
{
    public const string Formato = "dd/MM/yyyy";

    private const string Separadores = "/-. ";

    /// <summary>
    /// Da forma a lo tecleado o pegado: las barras entran solas cuando llega la cifra siguiente («101» → «10/1»), una barra
    /// escrita a mano cierra el grupo y completa con cero un día o mes de una cifra («1/» → «01/»), sobran las cifras de más y
    /// cualquier otro carácter. Una fecha ISO entera («2026-12-10», la que deja un <c>fill</c> o un pegado desde otra
    /// herramienta) se convierte a dd/mm/aaaa.
    /// </summary>
    public static string Aplicar(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return string.Empty;

        var iso = PatronIso().Match(texto);
        if (iso.Success) return $"{iso.Groups[3].Value}/{iso.Groups[2].Value}/{iso.Groups[1].Value}";

        var grupos = new[] { string.Empty, string.Empty, string.Empty };
        var i = 0;
        for (var k = 0; k < texto.Length; k++)
        {
            var c = texto[k];
            if (c is >= '0' and <= '9')
            {
                if (grupos[i].Length == (i < 2 ? 2 : 4))
                {
                    if (i == 2) continue;
                    i++;
                }

                grupos[i] += c;
            }
            else if (Separadores.Contains(c) && i < 2 && grupos[i].Length > 0)
            {
                // El cero solo se completa con la barra recién tecleada al final: en medio del texto (se está corrigiendo
                // una cifra) rellenar empujaría las demás y dejaría el campo más largo que la máscara.
                if (grupos[i].Length == 1 && k == texto.Length - 1) grupos[i] = "0" + grupos[i];
                i++;
            }
        }

        var resultado = grupos[0];
        if (i >= 1) resultado += "/" + grupos[1];
        if (i >= 2) resultado += "/" + grupos[2];
        return resultado;
    }

    /// <summary>
    /// La fecha que representa el texto, o <c>false</c> si está a medias o no existe (31/02/2026). Admite día y mes de una
    /// cifra («1/2/2026», pegado), que <see cref="Formatear"/> deja luego en dd/mm/aaaa; el año, siempre de cuatro.
    /// </summary>
    public static bool TryInterpretar(string? texto, out DateOnly fecha) =>
        DateOnly.TryParseExact(Aplicar(texto), "d/M/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha);

    /// <summary>Si el texto ya es la fecha entera dd/mm/aaaa: lo que se notifica mientras se escribe.</summary>
    public static bool EsCompleta(string? texto, out DateOnly fecha)
    {
        fecha = default;
        return Aplicar(texto).Length == Formato.Length && TryInterpretar(texto, out fecha);
    }

    public static string Formatear(DateOnly? fecha) =>
        fecha?.ToString(Formato, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>El valor de los campos que enlazan la fecha como texto ISO (los antiguos <c>input type="date"</c>).</summary>
    public static DateOnly? DesdeIso(string? iso) =>
        DateOnly.TryParseExact(iso?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha) ? fecha : null;

    public static string AIso(DateOnly? fecha) =>
        fecha?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    // [0-9] y no \d: en .NET \d casa también cifras de otros alfabetos, y el script del navegador solo admite las ASCII.
    [GeneratedRegex(@"^\s*([0-9]{4})-([0-9]{2})-([0-9]{2})\s*$")]
    private static partial Regex PatronIso();
}
