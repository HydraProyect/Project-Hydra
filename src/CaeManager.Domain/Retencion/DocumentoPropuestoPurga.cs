using CaeManager.Domain.Common;

namespace CaeManager.Domain.Retencion;

/// <summary>
/// Un Documento que la detección propuso purgar en una <see cref="SolicitudPurga"/> de
/// <see cref="TipoDatoPurgable.Documentos"/>. Congela el criterio (R4 de la decisión del 2026-11-18): con plazos por
/// Tenant propietario y por tipo de documento una sola fecha de corte ya no dice qué se propuso, así que la ejecución
/// purga exactamente esta lista, nunca más. Un cambio de plazo, una versión sustituida o un Documento subido después de
/// la detección no amplían una solicitud ya abierta. Sin navegación a la solicitud, por el mismo motivo que
/// <see cref="IncidenciaPurga"/>: el sellado del Tenant propietario.
/// </summary>
public class DocumentoPropuestoPurga : EntidadConTenant
{
    public Guid SolicitudPurgaId { get; private set; }

    public Guid DocumentoId { get; private set; }

    private DocumentoPropuestoPurga()
    {
        // Requerido por EF Core.
    }

    internal DocumentoPropuestoPurga(Guid solicitudPurgaId, Guid documentoId)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La propuesta debe nombrar el Documento.", nameof(documentoId));

        SolicitudPurgaId = solicitudPurgaId;
        DocumentoId = documentoId;
    }
}
