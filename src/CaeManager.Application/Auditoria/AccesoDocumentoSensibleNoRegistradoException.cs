namespace CaeManager.Application.Auditoria;

/// <summary>
/// El acceso a un recurso sensible no pudo registrarse (DEC-36, REC-208), y por eso el
/// contenido no se sirve: el registro falla en cerrado. Hoy solo ocurre si la base rechaza
/// el registro de una Sesión Privilegiada (sesión cerrada, caducada, de otro Tenant o de
/// otro actor) o si la conexión no tiene con qué escribirlo; el motivo queda en el
/// registro del servidor, no en el mensaje.
/// </summary>
public sealed class AccesoDocumentoSensibleNoRegistradoException(Guid recursoId)
    : Exception($"No se pudo registrar el acceso al recurso {recursoId}; el contenido no se sirve.")
{
    public Guid RecursoId { get; } = recursoId;
}
