using CaeManager.Domain.Common;

namespace CaeManager.Domain.DocumentosIa;

/// <summary>
/// Vínculo durable entre una <see cref="ExtraccionIaCache"/> y un
/// <c>Documento</c> (REC-036, DEC-34): la relación explícita que
/// <see cref="ExtraccionIaCache"/> declaraba pendiente en su propio
/// comentario de clase.
///
/// <b>Por qué es una tabla aparte y no un <c>DocumentoId</c> en
/// <see cref="ExtraccionIaCache"/>.</b> La caché se indexa por
/// <see cref="ExtraccionIaCache.HashSha256"/> — ese es su motivo de existir:
/// dos documentos con el mismo contenido comparten extracción y no se paga
/// dos veces al proveedor. El mismo fichero subido dos veces, o el mismo
/// certificado para dos trabajadores, produce legítimamente una única entrada
/// de caché referenciada por varios Documentos. Un <c>DocumentoId</c> único
/// en <see cref="ExtraccionIaCache"/> obligaría a elegir uno de los dos y
/// dejaría al otro sin vínculo — exactamente la fuga que esta tabla cierra.
///
/// <b>Cuándo se crea.</b> En <c>DocumentAIRouterService.ProcesarAsync</c>,
/// solo cuando se conoce un <c>documentoId</c> — es decir, cuando la llamada
/// viene de <c>VerificacionIaDocumentoService</c> sobre un Documento ya
/// existente. Las lecturas de mero triage previas a la creación del
/// Documento (detección de campos al subir, adjunto de correo/WhatsApp,
/// detección de campos de una Plantilla) no tienen todavía un Documento al
/// que enlazar — mismo caso que <see cref="AuditoriaExtraccionIa.DocumentoId"/>
/// documenta para sí misma, y no se resuelve aquí: esas entradas quedan sin
/// vínculo por diseño, no por descuido (ver el hallazgo secundario del
/// handoff HO-036-01 sobre la TTL pendiente para esa clase de entradas).
///
/// <b>Cuándo se borra.</b> Cuando <c>EjecucionPurgaService</c> anonimiza el
/// Documento (retención cumplida) — nunca en el borrado lógico reversible
/// (<c>Documento.MarcarComoEliminado</c>/<c>RestaurarDocumentoCommand</c>),
/// porque ahí el archivo origen sigue existiendo y purgar la caché sería
/// destruir un derivado de un dato que todavía no ha desaparecido. Al
/// desaparecer el último vínculo de una entrada, la entrada de
/// <see cref="ExtraccionIaCache"/> se borra con ella — "sin cachés
/// huérfanas" es literal de DEC-34.
/// </summary>
public class ExtraccionIaCacheDocumento : EntidadConTenant
{
    public Guid ExtraccionIaCacheId { get; private set; }
    public Guid DocumentoId { get; private set; }
    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private ExtraccionIaCacheDocumento()
    {
    }

    private ExtraccionIaCacheDocumento(Guid extraccionIaCacheId, Guid documentoId)
    {
        if (extraccionIaCacheId == Guid.Empty)
            throw new ArgumentException("El vínculo debe referenciar una entrada de caché.", nameof(extraccionIaCacheId));
        if (documentoId == Guid.Empty)
            throw new ArgumentException("El vínculo debe referenciar un Documento.", nameof(documentoId));

        ExtraccionIaCacheId = extraccionIaCacheId;
        DocumentoId = documentoId;
    }

    public static ExtraccionIaCacheDocumento Crear(Guid extraccionIaCacheId, Guid documentoId) =>
        new(extraccionIaCacheId, documentoId);
}
