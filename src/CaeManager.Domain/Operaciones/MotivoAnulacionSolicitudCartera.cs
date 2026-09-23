namespace CaeManager.Domain.Operaciones;

/// <summary>Por qué se anuló una solicitud sin resolverse.</summary>
public enum MotivoAnulacionSolicitudCartera
{
    /// <summary>El solicitante ya tenía una Asignación de Cartera universal vigente sobre la operación.</summary>
    YaEnCartera = 0,

    /// <summary>La Asignación de Operación que se pidió ya no está vigente.</summary>
    OperacionNoVigente = 1,

    /// <summary>El solicitante ya no es un Gestor CAE activo del Operador CAE.</summary>
    SolicitanteNoDisponible = 2,
}
