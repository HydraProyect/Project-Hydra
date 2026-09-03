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
    /// Correcto DENTRO de una sola llamada cuando dos Documentos de
    /// <paramref name="documentoIds"/> comparten una entrada: la entrada solo
    /// se borra si, tras quitar los vínculos de TODOS los Documentos del
    /// lote, no queda ningún vínculo de un Documento fuera del lote.
    ///
    /// <b>Riesgo conocido, no cerrado en este incremento (hallazgo de
    /// revisión Codex, REC-036/DEC-34).</b> La comprobación anterior lee
    /// antes de decidir, sin ningún bloqueo ni aislamiento serializable —
    /// segura frente a lecturas dentro de la MISMA llamada (la lectura de
    /// "vínculos fuera del lote" nunca mira las filas que esta misma llamada
    /// va a borrar, así que no importa que ese borrado no esté todavía
    /// volcado), pero NO frente a dos invocaciones de este método
    /// EJECUTÁNDOSE A LA VEZ en transacciones separadas: si los Documentos A
    /// y B comparten una entrada y cada purga solo conoce el suyo, las dos
    /// pueden ver el vínculo del otro como "todavía vivo" (ninguna ha hecho
    /// commit aún), borrar cada una su propio vínculo, y dejar la entrada con
    /// cero vínculos sin que ninguna la borre — huérfana para siempre,
    /// justo lo que "sin cachés huérfanas" prohíbe. El caso simétrico
    /// también existe: una purga puede decidir "sin vínculos restantes" justo
    /// cuando otro Documento activo confirma un vínculo nuevo, y el borrado
    /// de la entrada se lleva por delante ese vínculo recién creado (FK en
    /// cascada). <c>EjecucionPurgaService</c>/<c>SolicitudPurga</c> no tienen
    /// hoy ningún token de concurrencia optimista ni bloqueo que impida dos
    /// ejecuciones simultáneas de <c>EjecutarPurgaCommand</c> (p. ej. un
    /// doble clic, o dos personas con acceso administrativo) — es un hueco
    /// preexistente a este cambio, ampliado aquí porque hasta ahora ninguna
    /// operación dependía de leer el estado de OTRO Documento para decidir
    /// qué borrar. Cerrarlo de verdad exige aislamiento serializable o
    /// bloqueo explícito alrededor de todo <c>EjecucionPurgaService.EjecutarAsync</c>
    /// (no solo de este método), que es una unidad de trabajo distinta a "el
    /// ciclo de vida de una tabla" — devuelto a la Oficina de Reconciliación
    /// en el RETURN PACKAGE de HO-036-01, no decidido unilateralmente aquí.
    /// </summary>
    Task PurgarVinculadosADocumentosAsync(IReadOnlyCollection<Guid> documentoIds, CancellationToken cancellationToken = default);
}
