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

    /// <summary>
    /// Auditoría de seguridad del módulo (2026-08-30): a diferencia de
    /// TGSS/RLC/RNT/ITA, estas anclas nunca se calibraron con un PDF real de
    /// la AEAT — el propio comentario de esta clase ya lo declaraba. Sin este
    /// gate, un desajuste de regex podía coincidir por casualidad con datos
    /// del propietario y auto-validar un certificado que nadie verificó de
    /// verdad. Revertir a <c>true</c> solo tras calibrar con muestras reales
    /// (PLAN-FIRMA-DIGITAL-PDF.md, PR-6).
    /// </summary>
    public override bool Calibrado => false;

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"(?:C\.?S\.?V\.?|C.digo\s+Seguro\s+de\s+Verificaci.n)\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,40})",
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
