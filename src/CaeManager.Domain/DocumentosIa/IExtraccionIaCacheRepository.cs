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
}
