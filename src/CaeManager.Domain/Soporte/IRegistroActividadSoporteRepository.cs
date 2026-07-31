namespace CaeManager.Domain.Soporte;

public interface IRegistroActividadSoporteRepository
{
    void Agregar(RegistroActividadSoporte registro);

    /// <summary>
    /// Todo lo ocurrido en una ventana de acceso concreta, en orden — es la
    /// forma de reconstruir una visita de soporte para enseñársela a un
    /// cliente que pregunte.
    /// </summary>
    Task<IReadOnlyList<RegistroActividadSoporte>> ObtenerPorDelegacionAsync(
        Guid delegacionTenantId, CancellationToken cancellationToken = default);
}
