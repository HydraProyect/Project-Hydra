using CaeManager.Domain.Common;

namespace CaeManager.Domain.Reclamaciones;

/// <summary>
/// Uno de los Documentos incluidos en un lote de ReclamacionDocumental —
/// mismo patrón de entidad hija con TenantId propio que Mensaje/AdjuntoMensaje
/// en Comunicaciones. DocumentoId es una referencia suelta (sin navegación):
/// el Documento puede llegar a borrarse/reemplazarse después de reclamado sin
/// que eso invalide el historial de qué se pidió.
/// </summary>
public class ReclamacionDocumentalDocumento : EntidadConTenant
{
    public Guid ReclamacionDocumentalId { get; private set; }
    public Guid DocumentoId { get; private set; }

    private ReclamacionDocumentalDocumento()
    {
    }

    public ReclamacionDocumentalDocumento(Guid reclamacionDocumentalId, Guid documentoId)
    {
        if (reclamacionDocumentalId == Guid.Empty)
            throw new ArgumentException("Debe pertenecer a una reclamación.", nameof(reclamacionDocumentalId));
        if (documentoId == Guid.Empty)
            throw new ArgumentException("Debe referenciar un documento.", nameof(documentoId));

        ReclamacionDocumentalId = reclamacionDocumentalId;
        DocumentoId = documentoId;
    }
}
