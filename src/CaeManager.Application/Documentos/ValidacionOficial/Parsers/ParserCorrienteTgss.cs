using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Certificado de estar al corriente en las obligaciones con la Seguridad
/// Social (TGSS). Calibrado con confirmación directa del usuario sobre el
/// texto real del certificado: el resultado es siempre uno de los dos
/// literales exactos "El presente certificado tiene carácter POSITIVO" /
/// "…NEGATIVO" (NEGATIVO = existe deuda, se rechaza siempre); la fecha va
/// tras "Información obtenida a"; el CIF es el "Código de Empresario" con
/// un "0" pegado delante (ver <see cref="ParserDocumentoOficialBase.RegexCifComun"/>).
/// </summary>
public class ParserCorrienteTgss : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.CorrienteTgss;

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"(?:C\.?E\.?A\.?|C.digo\s+Electr.nico\s+de\s+Autenticidad)\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,40})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: true);

    protected override CampoAncla AnclaCif => new(RegexCifComun, Obligatorio: true);

    // "Información obtenida a" — admite tanto fecha numérica (04/08/2026)
    // como literal (4 de agosto de 2026); «.» donde iría la vocal
    // acentuada, el extractor pierde las tildes.
    protected override CampoAncla AnclaFechaEmision => new(
        new Regex(@"obtenida\s+a\s+(?:(?<valor>\d{1,2}\s+de\s+\p{L}+\s+de\s+\d{4})|(?<valor>\d{1,2}[/\-]\d{1,2}[/\-]\d{4}))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: true);

    // Literal exacto confirmado por el usuario: "El presente certificado
    // tiene carácter POSITIVO/NEGATIVO". Ancla corta y case-sensitive en la
    // palabra clave — POSITIVO/NEGATIVO siempre en mayúsculas en el propio
    // documento, así que no hace falta IgnoreCase para distinguirlos entre
    // sí (y evita falsos positivos si "positivo" aparece en minúsculas en
    // otro contexto del documento).
    protected override Regex PatronResultadoPositivo => new(
        @"certificado\s+tiene\s+car.cter\s+POSITIVO", RegexOptions.Compiled);

    protected override Regex PatronResultadoNegativo => new(
        @"certificado\s+tiene\s+car.cter\s+NEGATIVO", RegexOptions.Compiled);
}
