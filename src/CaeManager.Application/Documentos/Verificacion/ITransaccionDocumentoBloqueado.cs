namespace CaeManager.Application.Documentos.Verificacion;

/// <summary>
/// Transacción explícita que bloquea la fila de un Documento (y las de sus
/// RevisionIaDocumento) antes de ejecutar una operación, para que la
/// comprobación "¿sigue siendo el Documento que se analizó, y nadie lo
/// decidió a mano?" y la escritura del resultado automático ocurran sin que
/// una decisión manual concurrente pueda colarse entre las dos.
///
/// Qué serializa el bloqueo: cualquier comando manual que modifique el
/// Documento (renovar, corregir o aplicar una revisión, firmar, eliminar,
/// anonimizar) espera a que termine la operación, o la operación espera a
/// que él confirme y entonces ve su versión nueva; resolver una revisión
/// (que no toca el Documento) actualiza su fila de RevisionIaDocumento, que
/// también queda bloqueada.
/// </summary>
public interface ITransaccionDocumentoBloqueado
{
    /// <summary>
    /// Ejecuta <paramref name="operacion"/> dentro de la transacción, con la
    /// <c>Version</c> actual del Documento leída bajo el bloqueo — <c>null</c>
    /// si el Documento no existe, está eliminado o no es del Tenant
    /// propietario en curso. La operación debe guardar sus cambios
    /// (SaveChanges) antes de devolver: el commit va a continuación. Puede
    /// ejecutarse más de una vez si la estrategia de reintentos lo repite.
    /// </summary>
    Task<T> EjecutarAsync<T>(
        Guid documentoId, Func<Guid?, CancellationToken, Task<T>> operacion, CancellationToken cancellationToken = default);
}
