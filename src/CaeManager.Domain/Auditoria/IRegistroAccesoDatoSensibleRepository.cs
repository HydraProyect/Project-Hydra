namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Persiste en <see cref="RegistroAuditoria"/> la lectura de un dato cifrado en
/// reposo (<see cref="RegistroAuditoria.AccionAccesoDatoSensible"/>). Ver la
/// implementación en Infrastructure para qué hace ante cambios pendientes
/// ajenos en el contexto y ante un fallo al guardar (en ambos casos, lanza).
/// </summary>
public interface IRegistroAccesoDatoSensibleRepository
{
    Task GuardarAsync(RegistroAuditoria registro, CancellationToken cancellationToken = default);
}
