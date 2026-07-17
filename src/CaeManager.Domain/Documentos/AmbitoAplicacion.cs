namespace CaeManager.Domain.Documentos;

/// <summary>A quién pertenece un Documento de este TipoDocumento — ver Documento.DeTrabajador/DeCliente/DeEmpresa/DeVehiculo.</summary>
public enum AmbitoAplicacion
{
    Trabajador,
    Cliente,
    Empresa,
    Vehiculo
}
