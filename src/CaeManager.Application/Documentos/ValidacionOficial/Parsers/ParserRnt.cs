using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Relación Nominal de Trabajadores (RNT, Sistema RED). Calibrado con
/// muestras reales: es un documento tabular (etiquetas agrupadas, valores en
/// otra zona del texto) y el extractor pierde las tildes (salen caracteres
/// de sustitución) — por eso el periodo se extrae por forma del valor y las
/// anclas usan «.» donde iría una vocal acentuada. El documento identifica a
/// la empresa por CCC, no por CIF (CIF y huella quedan opcionales: sin CIF,
/// el cotejo de identidad cae a revisión — ver ValidacionDocumentoOficialService).
/// </summary>
public class ParserRnt : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.Rnt;

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"huella\s*(?:electr.nica)?\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,64})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: false);

    protected override CampoAncla AnclaCif => new(RegexCifComun, Obligatorio: false);

    protected override CampoAncla AnclaPeriodo => new(RegexPeriodoPorForma, Obligatorio: true);
}
