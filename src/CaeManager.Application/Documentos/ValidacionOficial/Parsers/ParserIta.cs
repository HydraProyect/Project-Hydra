using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Informe de Trabajadores en Alta (ITA, TGSS). El listado de trabajadores
/// no se extrae aquí (eso es la detección de altas/bajas por IA, Fase 36);
/// este parser saca lo cotejable. Calibración con muestras reales: el ITA
/// tampoco trae CIF en el texto (identidad por CCC) — CIF opcional; sin él,
/// el cotejo de identidad cae a revisión. Anclas con «.» donde iría una
/// vocal acentuada (el extractor pierde las tildes).
/// </summary>
public class ParserIta : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.Ita;

    protected override CampoAncla AnclaCif => new(RegexCifComun, Obligatorio: false);

    protected override CampoAncla AnclaFechaEmision => new(
        new Regex(@"(?:fecha\s*(?:de\s+emisi.n)?\s*[:\.]?\s*(?<valor>\d{1,2}[/\-]\d{1,2}[/\-]\d{4}))|(?:a\s+(?<valor>\d{1,2}\s+de\s+\p{L}+\s+de\s+\d{4}))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: false);

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"(?:huella|C\.?E\.?A\.?|C.digo\s+Electr.nico\s+de\s+Autenticidad)\s*(?:electr.nica)?\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,64})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: false);
}
