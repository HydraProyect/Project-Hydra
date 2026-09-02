namespace CaeManager.Domain.Plantillas;

public enum EstadoItemGeneracion
{
    Pendiente,
    Completado,

    /// <summary>
    /// DEC-5 (propietario, 2026-09-02): el documento del ítem se generó, pero
    /// algún campo obligatorio de la plantilla quedó vacío. Distinto de
    /// <see cref="Fallido"/> — hay documento— y de <see cref="Completado"/>
    /// — hay algo que mirar—. Los nombres de los campos van en
    /// <see cref="ItemGeneracionDocumento.Error"/>.
    /// </summary>
    CompletadoConAvisos,

    Fallido
}
