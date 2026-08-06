using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Certificado de estar al corriente de obligaciones tributarias (AEAT).
/// Anclas según la redacción pública conocida — pendientes de calibración
/// con muestras reales (plan, PR-6).
/// </summary>
public class ParserCorrienteAeat : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.CorrienteAeat;

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"(?:C\.?S\.?V\.?|C[oó]digo\s+Seguro\s+de\s+Verificaci[oó]n)\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,40})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: true);

    protected override CampoAncla AnclaCif => new(RegexCifComun, Obligatorio: true);

    protected override CampoAncla AnclaFechaEmision => new(
        new Regex(@"(?:a\s+(?<valor>\d{1,2}\s+de\s+\p{L}+\s+de\s+\d{4}))|(?:fecha\s*[:\.]?\s*(?<valor>\d{1,2}[/\-]\d{1,2}[/\-]\d{4}))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: true);

    protected override Regex PatronResultadoPositivo => new(
        @"se\s+encuentra\s+al\s+corriente\s+de\s+sus\s+obligaciones\s+tributarias|POSITIVO",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    protected override Regex PatronResultadoNegativo => new(
        @"no\s+se\s+encuentra\s+al\s+corriente|incumplimiento\s+de\s+obligaciones\s+tributarias|NEGATIVO",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
