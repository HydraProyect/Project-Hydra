namespace CaeManager.Domain.Documentos;

/// <summary>A quién pertenece un Documento de este TipoDocumento — ver Documento.DeTrabajador/DeCliente/DeEmpresa/DeVehiculo/DeProyecto.</summary>
public enum AmbitoAplicacion
{
    Trabajador,
    Cliente,
    Empresa,
    Vehiculo,

    /// <summary>Documentación de requerimiento de una obra/instalación nueva (ver <see cref="Proyectos.Proyecto"/>), distinta de la documentación de acceso de mantenimiento tradicional.</summary>
    Proyecto
}
