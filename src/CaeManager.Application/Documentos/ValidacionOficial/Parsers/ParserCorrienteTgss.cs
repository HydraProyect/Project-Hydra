using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Certificado de estar al corriente en las obligaciones con la Seguridad
/// Social (TGSS). Anclas según la redacción pública conocida — pendientes de
/// calibración con muestras reales (plan, PR-6).
/// </summary>
public class ParserCorrienteTgss : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.CorrienteTgss;

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"(?:C\.?E\.?A\.?|C.digo\s+Electr.nico\s+de\s+Autenticidad)\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,40})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: true);

    protected override CampoAncla AnclaCif => new(RegexCifComun, Obligatorio: true);

    protected override CampoAncla AnclaFechaEmision => new(
        new Regex(@"(?:a\s+(?<valor>\d{1,2}\s+de\s+\p{L}+\s+de\s+\d{4}))|(?:fecha\s*[:\.]?\s*(?<valor>\d{1,2}[/\-]\d{1,2}[/\-]\d{4}))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: true);

    protected override Regex PatronResultadoPositivo => new(
        @"no\s+tiene\s+pendiente\s+de\s+ingreso\s+ninguna\s+reclamaci.n|est.\s+al\s+corriente\s+en\s+el\s+pago",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Lookbehind en el primer literal: "tiene pendiente de ingreso" es
    // subcadena del positivo "NO tiene pendiente de ingreso" — sin él, el
    // negativo matchearía dentro de todos los certificados positivos.
    protected override Regex PatronResultadoNegativo => new(
        @"(?<!no\s)tiene\s+pendiente\s+de\s+ingreso|no\s+se\s+encuentra\s+al\s+corriente|NO\s+est.\s+al\s+corriente",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
