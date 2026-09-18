namespace CaeManager.Application.Common;

/// <summary>
/// Clasifica un correo para decidir qué pie institucional lleva (sistema de
/// correo TALVEG): el motivo por el que se recibe y cómo dejar de recibirlo,
/// obligatorio en los informativos y recomendable en el resto. No decide nada
/// de contenido — el <c>cuerpoHtml</c> lo sigue componiendo cada llamador; el
/// envoltorio de marca solo elige la plantilla de pie según este valor.
/// </summary>
public enum TipoAvisoCorreo
{
    /// <summary>Acción puntual que la propia persona disparó (alta, credenciales). Pie mínimo, sin explicar el motivo.</summary>
    Transaccional,

    /// <summary>Relacionado con el acceso a la cuenta (restablecer contraseña, acceso de soporte). Pie fijo: se envía siempre y no se puede desactivar.</summary>
    Seguridad,

    /// <summary>Resumen o notificación periódica (alertas, informes). Pie con el motivo de por qué llega. No hay hoy una preferencia por destinatario para desactivarlo.</summary>
    Informativo,
}
