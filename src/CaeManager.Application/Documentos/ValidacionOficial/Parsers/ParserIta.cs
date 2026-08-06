using System.Text.RegularExpressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.ValidacionOficial.Parsers;

/// <summary>
/// Informe de Trabajadores en Alta (ITA, TGSS). Anclas según la redacción
/// pública conocida — pendientes de calibración con muestras reales (plan,
/// PR-6). El listado de trabajadores en sí no se extrae aquí: eso es la
/// detección de altas/bajas por IA ya existente (Fase 36); este parser solo
/// saca lo necesario para el cotejo de identidad y periodo.
/// </summary>
public class ParserIta : ParserDocumentoOficialBase
{
    public override PerfilDocumentoOficial Perfil => PerfilDocumentoOficial.Ita;

    protected override CampoAncla AnclaCif => new(RegexCifComun, Obligatorio: true);

    protected override CampoAncla AnclaFechaEmision => new(
        new Regex(@"(?:fecha\s*(?:de\s+emisi[oó]n)?\s*[:\.]?\s*(?<valor>\d{1,2}[/\-]\d{1,2}[/\-]\d{4}))|(?:a\s+(?<valor>\d{1,2}\s+de\s+\p{L}+\s+de\s+\d{4}))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: false);

    protected override CampoAncla AnclaCodigoVerificacion => new(
        new Regex(@"(?:huella|C\.?E\.?A\.?|C[oó]digo\s+Electr[oó]nico\s+de\s+Autenticidad)\s*(?:electr[oó]nica)?\s*[:\.]?\s*(?<valor>[A-Z0-9][A-Z0-9\-]{8,64})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        Obligatorio: false);
}
