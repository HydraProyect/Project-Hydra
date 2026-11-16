using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Common.Archivos;

/// <summary>
/// Una pasada del barrido de archivos pendientes de referencia del Tenant propietario en curso (issue #2123, decisión de
/// 2026-11-16: opción A, borrado a las 24 h). Para cada fila apuntada hace más de <see cref="Antiguedad"/>: si un
/// Documento (también en la papelera) o una versión de plantilla referencia su clave, quita la fila y conserva el
/// archivo; si no lo referencia ninguno, borra el archivo y después la fila. Borrar primero el archivo hace que una
/// pasada interrumpida entre los dos pasos se complete en la siguiente (borrar un archivo que ya no está no falla).
///
/// <para>
/// Las 24 h dan margen a cualquier alta en curso: ninguna petición vive tanto, así que una fila tan antigua ya no tiene
/// un alta que vaya a confirmarse después de la comprobación. Lo que se comprueba es lo que hay en la base al barrer,
/// no lo que dijo el alta: un commit ambiguo que sí confirmó el Documento deja su clave referenciada y el archivo se
/// conserva.
/// </para>
/// </summary>
public sealed class BarridoArchivosPendientesDeReferencia(
    IArchivosPendientesDeReferencia pendientes,
    IFileStorageService almacenamiento,
    TimeProvider reloj,
    ILogger<BarridoArchivosPendientesDeReferencia> logger)
{
    /// <summary>La antigüedad mínima de una fila para que el barrido decida sobre su archivo (decisión de 2026-11-16).</summary>
    public static readonly TimeSpan Antiguedad = TimeSpan.FromHours(24);

    /// <summary>Filas por pasada: acota la memoria y la duración de una pasada; lo que no cabe queda para la siguiente.</summary>
    public const int MaximoPorPasada = 500;

    public async Task<ResultadoBarridoArchivosPendientes> BarrerAsync(CancellationToken cancellationToken)
    {
        var limiteUtc = reloj.GetUtcNow().UtcDateTime - Antiguedad;
        var apuntados = await pendientes.ObtenerApuntadosHastaAsync(limiteUtc, MaximoPorPasada, cancellationToken);

        // La ventana la decide este servicio, no solo la consulta: una fila más reciente que el límite no se toca aunque
        // la consulta la devolviera.
        var vencidos = apuntados.Where(p => p.GuardadoEnUtc <= limiteUtc).ToList();
        if (vencidos.Count == 0)
            return new ResultadoBarridoArchivosPendientes(0, 0, 0);

        var referenciadas = await pendientes.ReferenciadasAsync(vencidos.Select(p => p.Clave).Distinct().ToList(), cancellationToken);

        int borrados = 0, conservados = 0, fallidos = 0;
        foreach (var pendiente in vencidos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (referenciadas.Contains(pendiente.Clave))
                {
                    await pendientes.DescartarAsync(pendiente.Id, cancellationToken);
                    conservados++;
                }
                else
                {
                    await almacenamiento.EliminarAsync(pendiente.Clave, cancellationToken);
                    await pendientes.DescartarAsync(pendiente.Id, cancellationToken);
                    borrados++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Una fila que falla no detiene las demás: se queda y la siguiente pasada vuelve a decidir sobre ella.
                // Solo el Id de la fila y el tipo de la excepción: ni la clave ni el mensaje deben acabar en los registros.
                fallidos++;
                logger.LogWarning("No se pudo barrer el archivo pendiente {ArchivoPendienteId} ({TipoExcepcion}); se reintentará.",
                    pendiente.Id, ex.GetType().Name);
            }
        }

        return new ResultadoBarridoArchivosPendientes(borrados, conservados, fallidos);
    }
}

/// <summary>Lo que hizo una pasada: archivos borrados, filas descartadas conservando el archivo, y filas que fallaron.</summary>
public sealed record ResultadoBarridoArchivosPendientes(int Borrados, int Conservados, int Fallidos);
