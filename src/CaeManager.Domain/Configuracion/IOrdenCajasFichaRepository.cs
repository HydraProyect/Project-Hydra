namespace CaeManager.Domain.Configuracion;

public interface IOrdenCajasFichaRepository
{
    /// <summary>
    /// El orden de ese usuario para ese tipo de ficha en el Tenant actual, si lo
    /// tiene. El Tenant no es parámetro: lo acota el filtro global.
    /// </summary>
    Task<OrdenCajasFicha?> ObtenerAsync(Guid usuarioId, string tipoFicha, CancellationToken cancellationToken = default);

    void Agregar(OrdenCajasFicha orden);

    void Eliminar(OrdenCajasFicha orden);
}
