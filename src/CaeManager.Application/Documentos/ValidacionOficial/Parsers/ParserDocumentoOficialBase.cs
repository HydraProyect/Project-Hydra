using System.Globalization;
using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Base de los parsers de documento oficial: cada perfil define su tabla de
/// anclas (campo → regex con grupo <c>valor</c> → obligatorio) y esta base
/// aplica la mecánica común. Diseñado para calibrarse con PDFs reales en
/// sesión (plan, PR-6): ajustar un parser es editar los literales de su
/// tabla, sin tocar flujo ni tests de orquestación. Las anclas actuales
/// salen de la redacción pública conocida de cada documento — hasta la
/// calibración, un desajuste cae a "campos faltantes" ⇒ revisión humana,
/// nunca a un falso auto-validado.
/// </summary>
public abstract class ParserDocumentoOficialBase : IParserDocumentoOficial
{
    public abstract PerfilDocumentoOficial Perfil { get; }

    /// <summary>Regex con un grupo nombrado <c>valor</c>; si el campo es obligatorio y no matchea, el documento cae a revisión.</summary>
    protected sealed record CampoAncla(Regex Patron, bool Obligatorio);

    /// <summary>
    /// CIF de entidad (letra + 7 dígitos + control, con separadores
    /// opcionales) o NIF de autónomo (8 dígitos + letra), acotado por
    /// lookahead para no comerse la palabra siguiente. Común a todos los
    /// perfiles — la etiqueta que lo precede (CIF/NIF) la pone cada parser.
    /// </summary>
    protected static readonly Regex RegexCifComun = new(
        @"(?:C\.?I\.?F\.?|N\.?I\.?F\.?)\s*[:\.]?\s*(?<valor>(?:[A-Z][\-\.\s]?\d{7}[\-\.\s]?[0-9A-J]|\d{8}[\-\.\s]?[A-Z]))(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    protected virtual CampoAncla? AnclaCodigoVerificacion => null;
    protected virtual CampoAncla? AnclaCif => null;
    protected virtual CampoAncla? AnclaRazonSocial => null;
    protected virtual CampoAncla? AnclaFechaEmision => null;
    protected virtual CampoAncla? AnclaPeriodo => null;

    /// <summary>Solo certificados de estar al corriente: regex del literal positivo y regex del negativo explícito.</summary>
    protected virtual Regex? PatronResultadoPositivo => null;
    protected virtual Regex? PatronResultadoNegativo => null;

    public DatosDocumentoOficialExtraidos Extraer(string textoNormalizado)
    {
        var faltantes = new List<string>();

        var codigo = ExtraerCampo(textoNormalizado, AnclaCodigoVerificacion, "código de verificación", faltantes);
        var cif = ExtraerCampo(textoNormalizado, AnclaCif, "CIF", faltantes);
        var razonSocial = ExtraerCampo(textoNormalizado, AnclaRazonSocial, "razón social", faltantes);
        var fechaTexto = ExtraerCampo(textoNormalizado, AnclaFechaEmision, "fecha de emisión", faltantes);
        var periodoTexto = ExtraerCampo(textoNormalizado, AnclaPeriodo, "periodo de liquidación", faltantes);

        var fecha = NormalizarFecha(fechaTexto);
        if (fechaTexto is not null && fecha is null && AnclaFechaEmision!.Obligatorio)
            faltantes.Add("fecha de emisión (formato no reconocido)");

        bool? positivo = null;
        if (PatronResultadoPositivo is not null)
        {
            // El negativo se comprueba primero: la redacción negativa contiene
            // a veces la positiva como subcadena ("NO se encuentra al corriente...").
            positivo = PatronResultadoNegativo?.IsMatch(textoNormalizado) == true
                ? false
                : PatronResultadoPositivo.IsMatch(textoNormalizado)
                    ? true
                    : null;
            if (positivo is null)
                faltantes.Add("declaración de resultado (ni positivo ni negativo reconocibles)");
        }

        return new DatosDocumentoOficialExtraidos(
            NormalizarCodigo(codigo), NormalizarCif(cif), razonSocial, fecha, NormalizarPeriodo(periodoTexto),
            positivo, faltantes);
    }

    private static string? ExtraerCampo(string texto, CampoAncla? ancla, string nombre, List<string> faltantes)
    {
        if (ancla is null) return null;
        var match = ancla.Patron.Match(texto);
        if (match.Success) return match.Groups["valor"].Value.Trim();
        if (ancla.Obligatorio) faltantes.Add(nombre);
        return null;
    }

    /// <summary>Colapsa espacios y saltos: mitiga la fidelidad no garantizada del extractor de texto de PDFsharp y hace las anclas insensibles a la maquetación.</summary>
    public static string NormalizarTexto(IEnumerable<string> paginas) =>
        Regex.Replace(string.Join(" ", paginas), @"\s+", " ").Trim();

    private static string? NormalizarCodigo(string? codigo) =>
        string.IsNullOrWhiteSpace(codigo) ? null : Regex.Replace(codigo, @"\s", "").ToUpperInvariant();

    public static string? NormalizarCif(string? cif) =>
        string.IsNullOrWhiteSpace(cif) ? null : Regex.Replace(cif, @"[\s\.\-]", "").ToUpperInvariant();

    private static readonly string[] MesesEs =
    [
        "enero", "febrero", "marzo", "abril", "mayo", "junio",
        "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre",
    ];

    /// <summary>Acepta "04/08/2026", "04-08-2026" y "a 4 de agosto de 2026".</summary>
    public static DateOnly? NormalizarFecha(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;

        if (DateOnly.TryParseExact(texto.Trim(), ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
            return fecha;

        var literal = Regex.Match(texto, @"(?<dia>\d{1,2})\s+de\s+(?<mes>\p{L}+)\s+de\s+(?<anio>\d{4})",
            RegexOptions.IgnoreCase);
        if (!literal.Success) return null;

        var mes = Array.FindIndex(MesesEs, m => m.Equals(literal.Groups["mes"].Value, StringComparison.OrdinalIgnoreCase)) + 1;
        if (mes == 0) return null;

        return int.TryParse(literal.Groups["dia"].Value, out var dia) && int.TryParse(literal.Groups["anio"].Value, out var anio)
            && dia is >= 1 and <= 31
            ? new DateOnly(anio, mes, Math.Min(dia, DateTime.DaysInMonth(anio, mes)))
            : null;
    }

    /// <summary>"07/2026" o "2026-07" → "2026-07" (el formato de VerificacionDocumentoOficial.PeriodoDetectado).</summary>
    public static string? NormalizarPeriodo(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;

        var mesAnio = Regex.Match(texto, @"^(?<mes>\d{1,2})\s*/\s*(?<anio>\d{4})$");
        if (mesAnio.Success)
            return $"{mesAnio.Groups["anio"].Value}-{int.Parse(mesAnio.Groups["mes"].Value):00}";

        var anioMes = Regex.Match(texto, @"^(?<anio>\d{4})-(?<mes>\d{1,2})$");
        return anioMes.Success
            ? $"{anioMes.Groups["anio"].Value}-{int.Parse(anioMes.Groups["mes"].Value):00}"
            : null;
    }
}
