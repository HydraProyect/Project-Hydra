namespace CaeManager.Web.Services;

/// <summary>
/// Sanea valores controlados por quien hace la petición antes de ponerlos en una plantilla de log
/// (<c>cs/log-forging</c>): quita saltos de línea y demás caracteres de control para que no pueda fabricarse
/// una entrada falsa, y limita la longitud para que un valor enorme no infle el log.
/// </summary>
public static class ValoresParaLog
{
    private const int LongitudMaxima = 256;

    public static string Sanear(string? valor)
    {
        var sinSaltos = (valor ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        var limpio = string.Concat(sinSaltos.Where(c => !char.IsControl(c) && c is not '\u2028' and not '\u2029'));
        if (limpio.Length <= LongitudMaxima)
            return limpio;

        // No partir un par sustituto por la mitad.
        var corte = char.IsHighSurrogate(limpio[LongitudMaxima - 1]) ? LongitudMaxima - 1 : LongitudMaxima;
        return limpio[..corte];
    }
}