namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Acceso a la fila única de <see cref="OrdenMenuLateral"/>. Sin filtro de tenant: es una fila
/// del plano de Plataforma. Quién puede escribirla no lo decide este repositorio sino
/// <c>GuardarOrdenMenuLateralCommand</c> (capacidad AdminPlataforma global) y, debajo, la RLS
/// de la tabla.
/// </summary>
public interface IOrdenMenuLateralRepository
{
    /// <summary>La fila con seguimiento, para cambiarla.</summary>
    Task<OrdenMenuLateral?> ObtenerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// La fila sin seguimiento, para leerla y cachearla: una instancia trackeada de un circuito
    /// largo podría estar vieja y acabar en la caché global de todos los Tenants.
    /// </summary>
    Task<OrdenMenuLateral?> ObtenerSinSeguimientoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Da de alta la fila única y guarda. Devuelve <c>false</c> si otro guardado la creó entre la
    /// lectura y el alta (violación de la clave primaria canónica): es un conflicto de
    /// concurrencia, no un error.
    /// </summary>
    Task<bool> AgregarYGuardarAsync(OrdenMenuLateral orden, CancellationToken cancellationToken = default);
}
