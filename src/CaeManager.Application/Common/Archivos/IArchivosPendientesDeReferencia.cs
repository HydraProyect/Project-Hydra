using CaeManager.Domain.Archivos;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Common.Archivos;

/// <summary>
/// Outbox de archivos del Tenant propietario en curso que se guardaron antes de que exista el Documento o la versión de
/// plantilla que los referencia (issue #2123). Ver <see cref="ArchivoPendienteDeReferencia"/> y
/// <see cref="BarridoArchivosPendientesDeReferencia"/>.
/// </summary>
public interface IArchivosPendientesDeReferencia
{
    /// <summary>
    /// Apunta <paramref name="clave"/> con un guardado propio, ya confirmado al volver: sobrevive a que el alta que viene
    /// después falle o se deshaga. No toca ni confirma lo que el contexto del llamador tenga pendiente.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Sin Tenant propietario en curso, o dentro de una transacción ya abierta (la fila se desharía con ella).
    /// </exception>
    Task ApuntarAsync(string clave, CancellationToken cancellationToken = default);

    /// <summary>Quita la fila de <paramref name="clave"/> tras confirmarse el alta que la referencia. No borra el archivo.</summary>
    Task QuitarAsync(string clave, CancellationToken cancellationToken = default);

    /// <summary>Las filas apuntadas en o antes de <paramref name="limiteUtc"/>, las más antiguas primero, hasta <paramref name="maximo"/>.</summary>
    Task<IReadOnlyList<ArchivoPendienteDeReferencia>> ObtenerApuntadosHastaAsync(
        DateTime limiteUtc, int maximo, CancellationToken cancellationToken = default);

    /// <summary>
    /// De <paramref name="claves"/>, las que referencia algún Documento (también uno en la papelera) o alguna versión de
    /// plantilla del Tenant propietario en curso.
    /// </summary>
    /// <exception cref="InvalidOperationException">Sin Tenant propietario en curso: no se puede decidir que nada falte.</exception>
    Task<IReadOnlySet<string>> ReferenciadasAsync(IReadOnlyCollection<string> claves, CancellationToken cancellationToken = default);

    /// <summary>Quita la fila <paramref name="id"/>, tras decidir el barrido sobre su archivo.</summary>
    Task DescartarAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Los dos pasos que repiten todos los puntos que guardan un archivo antes de su entidad.</summary>
public static class ArchivosPendientesDeReferenciaExtensions
{
    /// <summary>
    /// Apunta el archivo recién guardado. Si no se puede apuntar, lo borra y deja pasar la excepción: todavía no existe
    /// ninguna entidad que pueda referenciarlo, así que borrarlo es seguro, y seguir sin apuntarlo lo dejaría huérfano sin
    /// rastro si el alta fallara después.
    /// </summary>
    public static async Task ApuntarOEliminarAsync(
        this IArchivosPendientesDeReferencia pendientes, IFileStorageService almacenamiento, string clave,
        ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await pendientes.ApuntarAsync(clave, cancellationToken);
        }
        catch (Exception ex)
        {
            // Solo el tipo: ni la clave del archivo ni el mensaje de la excepción deben acabar en los registros.
            logger.LogWarning("No se pudo apuntar el archivo recién guardado como pendiente de referencia ({TipoExcepcion}): se borra.",
                ex.GetType().Name);
            try
            {
                await almacenamiento.EliminarAsync(clave, CancellationToken.None);
            }
            catch (Exception exBorrado)
            {
                logger.LogWarning("No se pudo borrar el archivo que no se pudo apuntar ({TipoExcepcion}).", exBorrado.GetType().Name);
            }

            throw;
        }
    }

    /// <summary>
    /// Quita la fila tras confirmarse el alta. Sin el token de la petición y sin dejar escapar ninguna excepción: el alta
    /// ya está confirmada, y si la fila se queda, el barrido la descarta sin borrar el archivo porque ya está referenciado.
    /// </summary>
    public static async Task QuitarTrasConfirmarAsync(
        this IArchivosPendientesDeReferencia pendientes, string clave, ILogger logger)
    {
        try
        {
            await pendientes.QuitarAsync(clave, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("No se pudo quitar la fila de un archivo ya referenciado ({TipoExcepcion}); la descartará el barrido.",
                ex.GetType().Name);
        }
    }
}
