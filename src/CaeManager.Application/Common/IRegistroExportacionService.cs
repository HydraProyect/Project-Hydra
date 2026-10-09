namespace CaeManager.Application.Common;

/// <summary>
/// Deja rastro en la auditoría de la exportación a Excel de un listado con datos
/// personales. Decisión del propietario del 2026-10-08 (D2 de la auditoría de
/// capacidades de los listados): el Excel de Trabajadores lleva el DNI porque
/// quien puede exportar ya lo ve en pantalla; lo que la decisión S4 del
/// 2026-09-24 objetaba —una salida masiva del DNI «sin rastro de acceso»— se
/// cierra aquí.
///
/// <para>
/// Lo llama el endpoint <b>después</b> de generar el libro y <b>antes</b> de
/// entregarlo: si el registro no se puede guardar, la excepción sube y el
/// fichero no sale (fallo cerrado, mismo criterio que
/// <see cref="IRegistroAccesoDatoSensibleService"/>). Consecuencia conocida: una
/// Sesión Privilegiada de un Actor de Plataforma TALVEG, cuya conexión es de
/// solo lectura, no puede exportar un listado que deje este rastro.
/// </para>
/// </summary>
public interface IRegistroExportacionService
{
    /// <param name="entidadTipo">Nombre simple de la entidad exportada, igual que en el resto de la auditoría.</param>
    /// <param name="filas">Cuántas filas lleva el fichero.</param>
    /// <param name="criterios">
    /// Filtros y orden de la vista exportada; vacío si se exportó todo. Quien llama no pasa aquí
    /// texto libre del usuario (la búsqueda puede ser un DNI): solo que lo hubo.
    /// </param>
    /// <param name="cancellationToken">Cancelación.</param>
    Task RegistrarAsync(
        string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
        CancellationToken cancellationToken = default);
}
