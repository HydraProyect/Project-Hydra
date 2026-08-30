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
    /// CIF/NIF de la empresa, común a todos los perfiles. Calibrado con
    /// muestras reales y las aclaraciones del usuario: la TGSS lo presenta
    /// como "Código de Empresario" con un prefijo numérico pegado ("90" en
    /// ITA/RLC/RNT, "0" en el certificado de corriente) — el prefijo se
    /// admite y NO se captura (el grupo «valor» es solo el CIF). Dos ramas:
    /// con etiqueta (CIF/NIF/Código de Empresario, admite también NIF de
    /// autónomo) y por forma (CIF de entidad con prefijo, para los
    /// documentos tabulares donde la etiqueta va lejos del valor). Los
    /// lookarounds evitan capturar dentro de un token mayor (huella, CCC).
    /// </summary>
    protected static readonly Regex RegexCifComun = new(
        @"(?:(?:C\.?I\.?F\.?|N\.?I\.?F\.?|C.digo\s+de\s+Empresario)\s*[:\.]?\s*\d{0,4}[\-\.\s]?(?<valor>(?:[A-HJNP-SUVW]\d{7}[0-9A-J]|\d{8}[A-Z]))(?![A-Z0-9]))" +
        @"|(?:(?<![A-Z0-9])\d{0,4}(?<valor>[A-HJNP-SUVW]\d{7}[0-9A-J])(?![A-Z0-9]))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Periodo de liquidación por FORMA del valor (MM/yyyy), no por etiqueta:
    /// calibrado con RNT/RLC reales — son documentos tabulares donde las
    /// etiquetas van agrupadas y los valores en otra zona del texto extraído,
    /// así que la adyacencia etiqueta→valor no existe. Los lookarounds evitan
    /// pescar el "07/2026" de dentro de una fecha dd/MM/yyyy.
    /// </summary>
    protected static readonly Regex RegexPeriodoPorForma = new(
        @"(?<![\d/])(?<valor>(?:0?[1-9]|1[0-2])/20\d{2})(?![\d/])",
        RegexOptions.Compiled);

    protected virtual CampoAncla? AnclaCodigoVerificacion => null;
    protected virtual CampoAncla? AnclaCif => null;
    protected virtual CampoAncla? AnclaRazonSocial => null;
    protected virtual CampoAncla? AnclaFechaEmision => null;
    protected virtual CampoAncla? AnclaPeriodo => null;

    /// <summary>
    /// RNT/RLC (confirmado por el usuario): el documento no imprime una
    /// fecha de emisión propia — la fecha de emisión es siempre el día 1 del
    /// mes que indica el "Periodo de liquidación" (el periodo 06/2026 emite
    /// el 01/06/2026). La vigencia la calcula <c>CalculadoraEstadoDocumento</c>
    /// a partir de <c>TipoDocumento.VigenciaMeses</c> (3 meses para RLC/RNT en
    /// el seed — periodo 06/2026 vigente hasta ~31/08/2026), genérico y sin
    /// tocar aquí. Se deriva solo si no hay una fecha extraída por ancla.
    /// </summary>
    protected virtual bool FechaEmisionEsPrimerDiaDelPeriodo => false;

    /// <summary>
    /// El RLC/TC1 combinado con recibo de pago puede traer varias
    /// liquidaciones en un único archivo (confirmado por el usuario). Sin
    /// una muestra real para calibrar cómo se repiten los bloques, no se
    /// intenta emparejar RLC↔recibo — solo se detecta la ambigüedad (más de
    /// un periodo distinto en el texto) para mandar a revisión en vez de
    /// auto-validar a ciegas contra el primer periodo que matchee.
    /// </summary>
    protected virtual bool AdvertirSiMultiplesPeriodos => false;

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

        var periodoNormalizado = NormalizarPeriodo(periodoTexto);
        // Mismo criterio que la fecha de emisión (arriba): un valor que la
        // ancla capturó pero que no se pudo normalizar es un campo obligatorio
        // sin reconocer, no un periodo ausente — debe bloquear la
        // auto-validación igual que si no se hubiera encontrado nada.
        if (periodoTexto is not null && periodoNormalizado is null && AnclaPeriodo!.Obligatorio)
            faltantes.Add("periodo de liquidación (formato no reconocido)");
        if (fecha is null && FechaEmisionEsPrimerDiaDelPeriodo && periodoNormalizado is not null)
        {
            var partes = periodoNormalizado.Split('-');
            fecha = new DateOnly(int.Parse(partes[0]), int.Parse(partes[1]), 1);
        }

        // Distintos valores de periodo en el documento (no solo repeticiones
        // del mismo, típicas de cabeceras por página) delatan varias
        // liquidaciones bundladas en un único archivo.
        var multiplesLiquidaciones = AdvertirSiMultiplesPeriodos
            && RegexPeriodoPorForma.Matches(textoNormalizado)
                .Select(m => NormalizarPeriodo(m.Groups["valor"].Value))
                .Distinct()
                .Count() > 1;
        if (multiplesLiquidaciones)
            faltantes.Add("el archivo contiene varias liquidaciones (periodos distintos) — sube un documento por periodo");

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
            NormalizarCodigo(codigo), NormalizarCif(cif), razonSocial, fecha, periodoNormalizado,
            positivo, faltantes, multiplesLiquidaciones);
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

    /// <summary>Acepta "04/08/2026", "04-08-2026", "04 08 2026" (formato confirmado del ITA) y "a 4 de agosto de 2026".</summary>
    public static DateOnly? NormalizarFecha(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;

        if (DateOnly.TryParseExact(texto.Trim(), ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd MM yyyy", "d M yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
            return fecha;

        var literal = Regex.Match(texto, @"(?<dia>\d{1,2})\s+de\s+(?<mes>\p{L}+)\s+de\s+(?<anio>\d{4})",
            RegexOptions.IgnoreCase);
        if (!literal.Success) return null;

        var mes = Array.FindIndex(MesesEs, m => m.Equals(literal.Groups["mes"].Value, StringComparison.OrdinalIgnoreCase)) + 1;
        if (mes == 0) return null;

        // Auditoría de seguridad del módulo (2026-08-30): una fecha imposible
        // (31 de febrero) se rechaza — no se corrige al último día del mes.
        // Corregirla en silencio inventaría una fecha de emisión que el
        // documento no dice, y ese valor puede acabar cotejándose contra la
        // fecha que el usuario introdujo como si fuera un hecho fiable.
        return int.TryParse(literal.Groups["dia"].Value, out var dia) && int.TryParse(literal.Groups["anio"].Value, out var anio)
            && dia is >= 1 and <= 31 && dia <= DateTime.DaysInMonth(anio, mes)
            ? new DateOnly(anio, mes, dia)
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
