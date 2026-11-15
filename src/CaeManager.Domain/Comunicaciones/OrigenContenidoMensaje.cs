namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// De dónde sale el texto de un <see cref="Mensaje"/> saliente que envía una persona (issue #2007, D-5 del plan de cierre
/// de issues legales). Solo lo tienen los salientes de una persona: un entrante, un saliente automático y un mensaje
/// anterior a la columna lo dejan en null, que significa «no aplica o no consta», nunca <see cref="Persona"/>.
/// </summary>
public enum OrigenContenidoMensaje
{
    /// <summary>Lo escribió la persona que lo envía, sin partir de una respuesta sugerida.</summary>
    Persona = 0,

    /// <summary>Parte de una <see cref="RespuestaSugerida"/> de una macro que una persona revisó antes de enviar.</summary>
    MacroRevisada = 1,

    /// <summary>
    /// Parte de una <see cref="RespuestaSugerida"/> redactada por Vega que una persona revisó antes de enviar. Es el único
    /// origen que lleva la marca de contenido generado por IA en el correo saliente.
    /// </summary>
    VegaRevisada = 2
}
