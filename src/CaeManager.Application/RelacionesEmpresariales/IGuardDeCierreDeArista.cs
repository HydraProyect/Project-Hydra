namespace CaeManager.Application.RelacionesEmpresariales;

/// <summary>
/// Ver <see cref="GuardDeCierreDeArista"/> para el porqué de las dos
/// condiciones y por qué se evalúa sobre las bajas calculadas.
/// </summary>
public interface IGuardDeCierreDeArista
{
    /// <summary>
    /// ¿La arista (proveedora → cliente) sigue sosteniendo operación viva?
    /// Si devuelve <c>true</c>, cerrarla dejaría huérfana esa operación y el
    /// llamador debe rechazar la edición, no cerrar en silencio.
    /// </summary>
    Task<bool> TieneOperacionVivaAsync(
        Guid proveedoraId, Guid clienteId, CancellationToken cancellationToken = default);
}
