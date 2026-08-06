using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Relación Nominal de Trabajadores (RNT, Sistema RED). Lleva huella
/// electrónica propia además de la firma del PDF. Anclas pendientes de
/// calibración con muestras reales (plan, PR-6).
/// </summary>
public class ParserRnt : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.Rnt;

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"huella\s*(?:electr[oó]nica)?\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,64})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: true);

    protected override CampoAncla AnclaCif => new(RegexCifComun, Obligatorio: true);

    protected override CampoAncla AnclaPeriodo => new(
        new Regex(@"per[ií]odo\s+de\s+liquidaci[oó]n\s*[:\.]?\s*(?<valor>\d{1,2}\s*/\s*\d{4})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: true);
}
