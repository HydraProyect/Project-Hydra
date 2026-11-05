namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// De dónde partió el texto de un <see cref="Mensaje"/>, para poder demostrar después qué contenido saliente nació de una
/// sugerencia generada (Vega) y qué contenido lo escribió una persona. Lo fija el servidor a partir de la
/// <see cref="RespuestaSugerida"/> persistida; nunca lo manda el cliente.
/// </summary>
public enum OrigenTextoMensaje
{
    /// <summary>
    /// Escrito por una persona, sin pasar por una sugerencia. Es también el valor de todo entrante, de las filas anteriores
    /// a esta marca y de los salientes automáticos (acuse, paquete documental), cuyo texto no es una sugerencia.
    /// </summary>
    Persona = 0,

    /// <summary>Partió de una macro rellenada que una persona revisó antes de enviar (<see cref="OrigenRespuestaSugerida.Macro"/>).</summary>
    MacroRevisada = 1,

    /// <summary>Partió de una redacción de Vega, la IA, que una persona revisó antes de enviar (<see cref="OrigenRespuestaSugerida.Vega"/>).</summary>
    IaRevisada = 2
}