namespace CaeManager.Domain.DocumentosIa;

public interface IExtraccionIaCacheRepository
{
    /// <summary>
    /// Busca por la clave completa, no solo por el hash: la entrada guarda una
    /// interpretación del archivo bajo un tipo esperado y una versión del
    /// pipeline concretos, y servirla para otra pregunta o para otra versión
    /// devolvería una respuesta que nadie llegó a calcular. Ver
    /// <see cref="ExtraccionIaCache"/>.
    /// </summary>
    Task<ExtraccionIaCache?> ObtenerAsync(string hashSha256, string tipoEsperado, CancellationToken cancellationToken = default);

    void Agregar(ExtraccionIaCache cache);

    /// <summary>
    /// Retira una entrada previamente pasada a <see cref="Agregar"/> del
    /// seguimiento del contexto, sin borrar nada en base de datos — para
    /// cuando el <c>SaveChangesAsync</c> que la incluía falló porque otra
    /// ejecución concurrente ganó la misma carrera de caché (mismo hash,
    /// mismo tipo esperado, misma versión de pipeline) y su propio
    /// SaveChangesAsync llegó antes, chocando contra el índice único de
    /// <see cref="ExtraccionIaCache"/>.
    ///
    /// Necesario porque un SaveChangesAsync fallido NO revierte el estado
    /// "Added" de las entidades en el tracker: sin retirarla explícitamente,
    /// el siguiente intento de guardar (p. ej. para no perder la auditoría
    /// de una extracción que sí tuvo éxito) repetiría exactamente el mismo
    /// choque contra el índice único, indefinidamente. Ver
    /// <c>DocumentAIRouterService.RegistrarAuditoriaAsync</c>.
    /// </summary>
    void DescartarTrasConflicto(ExtraccionIaCache cache);

    /// <summary>
    /// Asegura el vínculo entre una entrada de caché y el Documento que la
    /// originó o la reutilizó (REC-036/DEC-34, ver
    /// <see cref="ExtraccionIaCacheDocumento"/>). Idempotente: si el vínculo
    /// ya existe (p. ej. una verificación IA repetida sobre el mismo
    /// Documento) no crea una fila duplicada y devuelve null — igual que
    /// <see cref="Agregar"/> no duplica el JSON de una entrada ya cacheada.
    /// Añade el vínculo al seguimiento del contexto sin guardarlo todavía;
    /// se persiste junto con la auditoría en el mismo <c>SaveChangesAsync</c>
    /// de <c>DocumentAIRouterService.RegistrarAuditoriaAsync</c>.
    /// </summary>
    Task<ExtraccionIaCacheDocumento?> VincularDocumentoAsync(
        Guid extraccionIaCacheId, Guid documentoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mismo motivo y mismo uso que <see cref="DescartarTrasConflicto"/>,
    /// pero para un vínculo pendiente de <see cref="VincularDocumentoAsync"/>
    /// cuyo <c>SaveChangesAsync</c> chocó contra el índice único de
    /// <see cref="ExtraccionIaCacheDocumento"/>.
    /// </summary>
    void DescartarVinculoTrasConflicto(ExtraccionIaCacheDocumento vinculo);

    /// <summary>
    /// Purga en cascada tras la anonimización de Documentos por retención
    /// (REC-036/DEC-34, invocado desde
    /// <c>EjecucionPurgaService.AnonimizarDocumentosAsync</c> — nunca desde
    /// el borrado lógico reversible, ver el comentario de
    /// <see cref="ExtraccionIaCacheDocumento"/>): borra los vínculos de los
    /// Documentos indicados y, de las entradas de <see cref="ExtraccionIaCache"/>
    /// que queden sin ningún vínculo restante (ni de estos Documentos ni de
    /// ningún otro), borra también la entrada — "sin cachés huérfanas" es
    /// literal de DEC-34.
    ///
    /// <paramref name="documentoIds"/> debe ser exactamente el conjunto de
    /// Documentos que de verdad se anonimizaron en este lote, no todos los
    /// candidatos: un Documento cuyo archivo no se pudo borrar se queda
    /// intacto (ver <c>EjecucionPurgaService</c>) y su caché no debe tocarse.
    /// Correcto también cuando dos Documentos de <paramref name="documentoIds"/>
    /// comparten una entrada: la entrada solo se borra si, tras quitar los
    /// vínculos de TODOS los Documentos del lote, no queda ningún vínculo de
    /// un Documento fuera del lote.
    /// </summary>
    Task PurgarVinculadosADocumentosAsync(IReadOnlyCollection<Guid> documentoIds, CancellationToken cancellationToken = default);
}
