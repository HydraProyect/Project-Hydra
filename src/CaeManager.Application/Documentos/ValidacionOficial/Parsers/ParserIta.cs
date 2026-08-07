using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Informe de Trabajadores en Alta (ITA, TGSS). El listado de trabajadores
/// no se extrae aquí (eso es la detección de altas/bajas por IA, Fase 36);
/// este parser saca lo cotejable. Calibración con muestras reales y
/// confirmación del usuario: el CIF aparece como "Código de Empresario" con
/// un prefijo numérico pegado delante —
/// <see cref="ParserDocumentoOficialBase.RegexCifComun"/> lo admite y lo
/// descarta. La fecha de emisión va tras el literal "Informe de Trabajadores
/// en Alta a fecha", con el día/mes/año separados por espacio (además de
/// admitirse "/" o "-", por si la maquetación varía). Anclas con «.» donde
/// iría una vocal acentuada (el extractor pierde las tildes).
/// </summary>
public class ParserIta : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.Ita;

    protected override CampoAncla AnclaCif => new(RegexCifComun, Obligatorio: true);

    protected override CampoAncla AnclaFechaEmision => new(
        new Regex(@"Trabajadores\s+en\s+Alta\s+a\s+fecha\s*[:\.]?\s*(?<valor>\d{1,2}[/\-\s]\d{1,2}[/\-\s]\d{4})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: true);

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"(?:huella|C\.?E\.?A\.?|C.digo\s+Electr.nico\s+de\s+Autenticidad)\s*(?:electr.nica)?\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,64})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: false);
}
