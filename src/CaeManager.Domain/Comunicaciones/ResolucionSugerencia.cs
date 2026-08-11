namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Qué hizo el Gestor con una sugerencia de la IA. Antes solo se guardaba
/// <c>Resuelta</c> (un bool que no distinguía aceptar de descartar), y sin esa
/// distinción el índice de palanca IA — cuánto trabajo se resuelve de un clic —
/// no se puede calcular.
///
/// La diferencia entre <see cref="Confirmada"/> y <see cref="ConfirmadaConEdicion"/>
/// es la métrica: una sugerencia que hay que corregir antes de aceptarla ahorra
/// menos que una que se acepta tal cual, aunque las dos acaben en la misma acción.
/// </summary>
public enum ResolucionSugerencia
{
    /// <summary>Aceptada tal cual, sin tocar ningún campo propuesto.</summary>
    Confirmada = 0,

    /// <summary>Aceptada, pero el Gestor cambió algún dato antes de confirmar.</summary>
    ConfirmadaConEdicion = 1,

    /// <summary>Descartada: la IA se equivocó o el correo no pedía lo que parecía.</summary>
    Descartada = 2
}
