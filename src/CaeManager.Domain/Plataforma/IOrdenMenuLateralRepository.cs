namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Acceso a la fila única de <see cref="OrdenMenuLateral"/>. Sin filtro de tenant: es una fila
/// del plano de Plataforma. Quién puede escribirla no lo decide este repositorio sino
/// <c>GuardarOrdenMenuLateralCommand</c> (capacidad AdminPlataforma global) y, debajo, la RLS
/// de la tabla.
/// </summary>
public interface IOrdenMenuLateralRepository
{
    Task<OrdenMenuLateral?> ObtenerAsync(CancellationToken cancellationToken = default);

    void Agregar(OrdenMenuLateral orden);
}
