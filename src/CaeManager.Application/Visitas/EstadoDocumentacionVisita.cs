namespace CaeManager.Application.Visitas;

/// <summary>
/// Lo que la columna «Documentación» del listado de Visitas dice de cada una. Los cuatro valores
/// parten la lista: una Visita está en uno y solo en uno. No es un estado guardado: se deduce de
/// <c>Visita.EstaCancelada</c>, de la modalidad de gestión CAE del Centro y de
/// <c>Visita.DocumentacionGestionadaEnUtc</c>, en ese orden. El nombre de cada valor es el que
/// viaja en la URL del listado (<c>?estado=PorGestionar,Gestionada</c>).
/// </summary>
public enum EstadoDocumentacionVisita
{
    /// <summary>Ni cancelada ni en un Centro sin gestión CAE, y sin marca de documentación gestionada.</summary>
    PorGestionar = 0,

    /// <summary>Se envió el paquete de acreditación o se marcó a mano.</summary>
    Gestionada = 1,

    /// <summary>El Centro no requiere gestión CAE: no hay documentación que gestionar.</summary>
    SinGestionCae = 2,

    /// <summary>La Visita está cancelada.</summary>
    Cancelada = 3,
}
