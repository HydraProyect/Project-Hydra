namespace CaeManager.Domain.Trabajadores;

/// <summary>Ver <see cref="DeteccionTrabajador"/>: trabajador visto en el documento pero sin alta en el sistema, o trabajador activo en el sistema pero ausente del documento.</summary>
public enum TipoDeteccion
{
    Nuevo,
    Ausente
}
