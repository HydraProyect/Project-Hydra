using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Recibo de Liquidación de Cotizaciones (RLC/TC1, Sistema RED). Cubre
/// también el tipo combinado "RLC/TC1 + Recibo de pago". Mismo hallazgo de
/// calibración que el RNT (documento tabular, tildes rotas por el extractor,
/// identidad por CCC): periodo por forma del valor, CIF y huella opcionales.
/// </summary>
public class ParserRlc : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.Rlc;

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"huella\s*(?:electr.nica)?\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,64})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: false);

    protected override CampoAncla AnclaCif => new(RegexCifComun, Obligatorio: false);

    protected override CampoAncla AnclaPeriodo => new(RegexPeriodoPorForma, Obligatorio: true);
}
