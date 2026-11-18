using System.Globalization;
using System.Numerics;
using System.Text;
using CaeManager.Application.Inbound.CredencialesAcceso;

namespace CaeManager.Infrastructure.Inbound;

/// <summary>
/// Forma del token de la credencial de acceso: <c>v1.kid.credencialId.serie.centros.firma</c>. Solo
/// serialización; la criptografía vive en <see cref="FirmanteCredencialAcceso"/>. El análisis es <b>canónico</b>: un
/// token solo se acepta si al volver a serializar su contenido sale exactamente la misma cadena. Sin eso, un cambio
/// en los bits sobrantes del último carácter base64url, o un Guid en mayúsculas, darían un token distinto con la
/// misma firma válida. ECDSA es además maleable ((r, s) y (r, n−s) verifican ambas), por lo que se exige s baja: la cadena
/// firmada es única por emisión, pero el mismo contenido firmado dos veces da firmas distintas (ECDSA es probabilística):
/// la cadena del token NUNCA es identificador del contenido; el identificador es la credencial y su serie.
/// </summary>
internal static class FormatoTokenCredencial
{
    public const string Version = "v1";
    public const int LongitudFirmaBytes = 64; // r‖s de P-256.

    /// <summary>La parte firmada: todo lo anterior al último punto.</summary>
    public static string Cuerpo(string kid, Guid credencialId, int serie, IReadOnlyList<CentroConFechaMaxima> centros) =>
        string.Join('.',
            Version,
            kid,
            credencialId.ToString("N"),
            serie.ToString(CultureInfo.InvariantCulture),
            string.Join(',', centros.Select(c =>
                $"{c.CentroId:N}:{c.FechaMaxima.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}")));

    public static string Componer(string cuerpo, byte[] firma) => cuerpo + "." + Base64Url(firma);

    /// <summary>Contenido, cuerpo firmado y firma; <c>null</c> ante cualquier defecto de forma.</summary>
    public static (CredencialVerificada Contenido, string Cuerpo, byte[] Firma)? Analizar(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096)
            return null;

        var partes = token.Split('.');
        if (partes.Length != 6 || partes[0] != Version)
            return null;

        var kid = partes[1];
        if (!EsKidValido(kid))
            return null;

        if (!Guid.TryParseExact(partes[2], "N", out var credencialId) || credencialId == Guid.Empty)
            return null;

        if (!int.TryParse(partes[3], NumberStyles.None, CultureInfo.InvariantCulture, out var serie) || serie < 1)
            return null;

        var centros = new List<CentroConFechaMaxima>();
        foreach (var entrada in partes[4].Split(','))
        {
            var par = entrada.Split(':');
            if (par.Length != 2
                || !Guid.TryParseExact(par[0], "N", out var centroId) || centroId == Guid.Empty
                || !DateOnly.TryParseExact(par[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
                return null;
            centros.Add(new CentroConFechaMaxima(centroId, fecha));
        }

        if (centros.Select(c => c.CentroId).Distinct().Count() != centros.Count)
            return null;

        var firma = DesdeBase64Url(partes[5]);
        if (firma is null || firma.Length != LongitudFirmaBytes || TieneSAlta(firma))
            return null; // s alta: la maleabilidad (r, n−s) daría otra cadena con la misma validez; no se emite ni se acepta.

        var cuerpo = Cuerpo(kid, credencialId, serie, centros);
        if (!string.Equals(Componer(cuerpo, firma), token, StringComparison.Ordinal))
            return null; // No canónico: mayúsculas, ceros a la izquierda, bits sobrantes del base64url…

        return (new CredencialVerificada(kid, credencialId, serie, centros), cuerpo, firma);
    }

    public static bool EsKidValido(string kid) =>
        kid.Length is >= 8 and <= 32 && kid.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static string Base64Url(byte[] datos) =>
        Convert.ToBase64String(datos).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? DesdeBase64Url(string texto)
    {
        if (texto.Length == 0 || texto.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            return null;

        var relleno = texto.Replace('-', '+').Replace('_', '/');
        relleno = relleno.PadRight(relleno.Length + (4 - relleno.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(relleno);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static readonly BigInteger OrdenP256 = BigInteger.Parse(
        "0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", NumberStyles.HexNumber);

    private static BigInteger S(byte[] firma) => new(firma.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);

    public static bool TieneSAlta(byte[] firma) => S(firma) > OrdenP256 / 2;

    /// <summary>Sustituye s por n−s si es alta: la forma única (s baja) hace que cada contenido tenga una sola cadena firmada por ejecución.</summary>
    public static byte[] NormalizarSBaja(byte[] firma)
    {
        if (!TieneSAlta(firma))
            return firma;

        var baja = (OrdenP256 - S(firma)).ToByteArray(isUnsigned: true, isBigEndian: true);
        var resultado = (byte[])firma.Clone();
        Array.Clear(resultado, 32, 32);
        baja.CopyTo(resultado, 64 - baja.Length);
        return resultado;
    }

    public static byte[] Bytes(string cuerpo) => Encoding.UTF8.GetBytes(cuerpo);
}
