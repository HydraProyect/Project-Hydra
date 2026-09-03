namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Solo escritura, a propósito — mismo criterio que
/// <see cref="RegistroAuditoria"/>: la lectura del rastro no pasa por un
/// repositorio de agregado (que cualquier handler podría inyectar sin pasar
/// por la superficie de consulta acotada), sino por
/// <c>IAuditoriaQueryContext.RegistrosAccesoDocumentoSensible</c>, expuesto
/// solo a la query que la sirve.
/// </summary>
public interface IRegistroAccesoDocumentoSensibleRepository
{
    void Agregar(RegistroAccesoDocumentoSensible registro);
}
