namespace CaeManager.Domain.Centros;

/// <summary>
/// Si un Centro de Trabajo exige gestión CAE a quienes acuden a él (P1-X2,
/// decisión del propietario del 2026-09-24).
///
/// Es una propiedad del Centro, no de sus canales: el canal
/// (<see cref="TipoCanalGestion"/>) decide <i>cómo se entrega</i> la
/// documentación; esta modalidad decide <i>si se exige</i>. Un Centro
/// <see cref="SinGestionCae"/> no exige ningún documento —al Trabajador solo le
/// hace falta llegar— y la Visita se comunica con un aviso (fecha, hora y quién
/// acude), no con un paquete de acreditación.
///
/// Se persiste como entero: los valores son estables.
/// </summary>
public enum ModalidadGestionCae
{
    /// <summary>El Centro exige documentación CAE — el caso de siempre.</summary>
    ConGestionCae = 0,

    /// <summary>El Centro no exige documentación CAE: basta con avisar de la Visita.</summary>
    SinGestionCae = 1
}
