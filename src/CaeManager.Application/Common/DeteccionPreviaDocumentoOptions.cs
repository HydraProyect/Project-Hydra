namespace CaeManager.Application.Common;

/// <summary>
/// Kill switch de <see cref="Documentos.Queries.DetectarCamposDocumento.DetectarCamposDocumentoQuery"/>
/// (hallazgo P0-4 de docs/business/MATURITY_REVIEW.md). Esa detección envía
/// el PDF completo a un proveedor de IA externo (Anthropic/Mistral/Gemini)
/// **antes** de que el usuario elija el tipo de documento — así que un
/// reconocimiento médico subido en Ámbito Trabajador sale hacia el
/// proveedor sin que ningún interruptor de <c>TipoDocumento</c> lo proteja
/// (esos solo actúan una vez conocido el tipo). Mientras no exista DPA de
/// subencargado que declare este tratamiento para datos de salud (art. 9
/// RGPD), esta detección queda apagada por defecto — el alta manual de
/// Documento sigue funcionando exactamente igual, solo sin la sugerencia
/// automática de trabajador/tipo. Actívese explícitamente
/// (<c>DeteccionPreviaDocumento:Activa=true</c>) solo cuando el DPA cubra
/// este envío.
/// </summary>
public class DeteccionPreviaDocumentoOptions
{
    public const string SeccionConfiguracion = "DeteccionPreviaDocumento";

    public bool Activa { get; set; }
}
