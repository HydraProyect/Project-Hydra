using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Relación Nominal de Trabajadores (RNT, Sistema RED). Calibrado con
/// muestras reales: es un documento tabular (etiquetas agrupadas, valores en
/// otra zona del texto) y el extractor pierde las tildes (salen caracteres
/// de sustitución) — por eso el periodo se extrae por forma del valor y las
/// anclas usan «.» donde iría una vocal acentuada. El CIF aparece como
/// "Código de Empresario" con un prefijo numérico pegado delante (confirmado
/// por el usuario) — <see cref="ParserDocumentoOficialBase.RegexCifComun"/>
/// lo admite y lo descarta.
/// </summary>
public class ParserRnt : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.Rnt;

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"huella\s*(?:electr.nica)?\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,64})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: false);

    protected override CampoAncla AnclaCif => new(RegexCifComun, Obligatorio: true);

    protected override CampoAncla AnclaPeriodo => new(RegexPeriodoPorForma, Obligatorio: true);

    // Confirmado por el usuario: el RNT no imprime fecha de emisión propia —
    // es el día 1 del mes del periodo de liquidación.
    protected override bool FechaEmisionEsPrimerDiaDelPeriodo => true;
}
