namespace CaeManager.Domain.Retencion;

/// <summary>
/// Categoría de una <see cref="IncidenciaPurga"/>. Deliberadamente no es un
/// "Fallido" genérico: un fallo técnico de supresión (este tipo) no es lo
/// mismo que un dato conservado porque aplica una excepción legal a la
/// supresión (art. 17.3 RGPD) — ese segundo caso no tiene hoy ningún camino
/// en el código (la detección de candidatos solo mira plazos de retención) y
/// no se modela todavía. Cuando exista, será un valor distinto de este,
/// nunca el mismo: mezclar ambos bajo una sola etiqueta impediría distinguir
/// "hay que reintentar" de "no hay que suprimir esto".
/// </summary>
public enum TipoIncidenciaPurga
{
    /// <summary>No se pudo eliminar el archivo del almacenamiento externo.</summary>
    FalloEliminacionArchivo
}
