using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Recibo de Liquidación de Cotizaciones (RLC/TC1, Sistema RED). Cubre
/// también el tipo combinado "RLC/TC1 + Recibo de pago". Mismo hallazgo de
/// calibración que el RNT (documento tabular, tildes rotas por el extractor):
/// periodo por forma del valor, CIF como "Código de Empresario" con prefijo
/// numérico (confirmado por el usuario, admitido y descartado por
/// <see cref="ParserDocumentoOficialBase.RegexCifComun"/>), huella opcional.
/// </summary>
public class ParserRlc : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.Rlc;

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"huella\s*(?:electr.nica)?\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,64})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: false);

    protected override CampoAncla AnclaCif => new(RegexCifComun, Obligatorio: true);

    protected override CampoAncla AnclaPeriodo => new(RegexPeriodoPorForma, Obligatorio: true);

    // Confirmado por el usuario: el RLC no imprime fecha de emisión propia —
    // es el día 1 del mes del periodo de liquidación.
    protected override bool FechaEmisionEsPrimerDiaDelPeriodo => true;

    // Confirmado por el usuario: el tipo "RLC/TC1 + Recibo de pago" puede
    // traer varias liquidaciones (varios periodos) en un único archivo, sin
    // entidad bancaria fija en el recibo. El cotejo cruzado RLC↔recibo
    // (razón social + importe + número/código de liquidación) exige una
    // muestra real para calibrar cómo se repiten los bloques — pendiente
    // (PR-7). Hasta entonces: detectar la ambigüedad y mandar a revisión.
    protected override bool AdvertirSiMultiplesPeriodos => true;
}
