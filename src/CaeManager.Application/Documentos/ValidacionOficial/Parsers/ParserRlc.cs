using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Recibo de Liquidación de Cotizaciones (RLC/TC1, Sistema RED). Cubre
/// también el tipo combinado "RLC/TC1 + Recibo de pago": el recibo bancario
/// añadido no aporta anclas y no estorba — si la calibración con muestras
/// reales pide un ancla extra del recibo, se añade entonces (plan, PR-6).
/// </summary>
public class ParserRlc : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.Rlc;

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
