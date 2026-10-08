namespace CaeManager.Web.Components.DesignSystem;

public enum VentanaContextoColocacion
{
    /// <summary>Sobre el disparador y centrada: el comportamiento de siempre.</summary>
    ArribaCentro,

    /// <summary>Bajo el disparador y centrada: recuentos dentro de una fila de listado.</summary>
    AbajoCentro,

    /// <summary>
    /// Bajo el disparador y alineada a su borde final: última columna de un
    /// listado, donde centrada se saldría de la tabla.
    /// </summary>
    AbajoFin
}
