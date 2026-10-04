using CaeManager.Domain.Common;

namespace CaeManager.Domain.Reclamaciones;

/// <summary>
/// Una de las líneas de un lote de ReclamacionDocumental — mismo patrón de entidad hija con TenantId propio que
/// Mensaje/AdjuntoMensaje en Comunicaciones. Hay dos formas, y solo dos (<c>CK_ReclamacionesDocumentalesDocumentos_Forma</c>):
/// <list type="bullet">
/// <item><b>Un Documento que existe</b> (<see cref="DocumentoId"/> informado, <see cref="TipoDocumentoId"/> y
/// <see cref="TrabajadorId"/> nulos): el que vence dentro de la ventana de reclamación, o el «Sin confirmar» sin fecha. Es
/// una referencia suelta (sin navegación): el Documento puede llegar a borrarse/reemplazarse después de reclamado sin que
/// eso invalide el historial de qué se pidió.</item>
/// <item><b>Un documento que falta</b> (<see cref="DocumentoId"/> nulo): nunca se subió, así que no hay Documento al que
/// apuntar. La línea guarda lo que se pidió: el <see cref="TipoDocumentoId"/> y, cuando el documento es de Trabajador, el
/// <see cref="TrabajadorId"/> (en una reclamación a una Empresa titular el sujeto es la propia Empresa de la reclamación).</item>
/// </list>
/// En las dos formas la reclamación cuenta igual: mismo registro, misma «última reclamación», mismo seguimiento de respuesta.
/// </summary>
public class ReclamacionDocumentalDocumento : EntidadConTenant
{
    public Guid ReclamacionDocumentalId { get; private set; }

    /// <summary>El Documento reclamado; nulo cuando lo reclamado es un documento que falta (<see cref="TipoDocumentoId"/> informado).</summary>
    public Guid? DocumentoId { get; private set; }

    /// <summary>Solo en un documento que falta: el Tipo de documento que se pidió.</summary>
    public Guid? TipoDocumentoId { get; private set; }

    /// <summary>Solo en un documento que falta y es de Trabajador: a quién le falta. Nulo si es de Empresa (el sujeto es la Empresa de la reclamación).</summary>
    public Guid? TrabajadorId { get; private set; }

    /// <summary>Lo reclamado es un documento que nunca se subió: no hay Documento.</summary>
    public bool EsDocumentoQueFalta => DocumentoId is null;

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

    private ReclamacionDocumentalDocumento(Guid reclamacionDocumentalId, Guid tipoDocumentoId, Guid? trabajadorId)
    {
        ReclamacionDocumentalId = reclamacionDocumentalId;
        TipoDocumentoId = tipoDocumentoId;
        TrabajadorId = trabajadorId;
    }

    /// <summary>Línea de un documento que falta: el Tipo pedido y, si es de Trabajador, a quién le falta.</summary>
    public static ReclamacionDocumentalDocumento DeDocumentoQueFalta(Guid reclamacionDocumentalId, Guid tipoDocumentoId, Guid? trabajadorId)
    {
        if (reclamacionDocumentalId == Guid.Empty)
            throw new ArgumentException("Debe pertenecer a una reclamación.", nameof(reclamacionDocumentalId));
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("Debe referenciar un tipo de documento.", nameof(tipoDocumentoId));
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("El trabajador, si se informa, no puede ser vacío.", nameof(trabajadorId));

        return new ReclamacionDocumentalDocumento(reclamacionDocumentalId, tipoDocumentoId, trabajadorId);
    }
}
