namespace CaeManager.Domain.Soporte;

/// <summary>Qué clase de rastro deja una sesión de soporte.</summary>
public enum TipoActividadSoporte
{
    /// <summary>Se abrió la ventana de acceso a un tenant. Lleva el motivo.</summary>
    AccesoConcedido,

    /// <summary>Se entró efectivamente en el workspace del tenant.</summary>
    WorkspaceActivado,

    /// <summary>Navegación a una pantalla.</summary>
    Navegacion,

    /// <summary>Interacción con un elemento de la interfaz.</summary>
    Interaccion,

    /// <summary>Se cerró el acceso, por revocación o por caducidad.</summary>
    AccesoRevocado,

    /// <summary>
    /// Lectura de datos del Tenant objetivo bajo una Sesión Privilegiada (issue #2001). La escribe
    /// solo la función <c>app_registrar_actividad_soporte_por_sesion</c>; el detalle nombra la
    /// operación que leyó, nunca los datos leídos.
    /// </summary>
    Lectura
}
