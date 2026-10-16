using CaeManager.Domain.Common;

namespace CaeManager.Domain.Proyectos;

/// <summary>
/// Un Tipo de Documento que un Proyecto de tipo <see cref="TipoProyecto.Evento"/> pide a quien entra en él.
/// Sin ciclo de vida propio, igual que <c>TipoDocumentoCentro</c>: quitarlo es una baja física. Mientras el evento
/// aporta requisitos (<see cref="VigenciaEvento.AportaRequisitosEn"/>), cuenta para los voluntarios que participan
/// en él: en los avisos de faltantes y en el estado y el cumplimiento del Centro del evento, salvo que el Centro no
/// tenga gestión CAE. Solo admite tipos de ámbito Trabajador. Todavía no interviene en el bloqueo de acceso.
/// </summary>
public class ProyectoRequisito : EntidadConTenant
{
    public Guid ProyectoId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }

    private ProyectoRequisito()
    {
    }

    public ProyectoRequisito(Guid proyectoId, Guid tipoDocumentoId)
    {
        if (proyectoId == Guid.Empty)
            throw new ArgumentException("El requisito debe pertenecer a un proyecto.", nameof(proyectoId));
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("El requisito debe tener un tipo de documento.", nameof(tipoDocumentoId));

        ProyectoId = proyectoId;
        TipoDocumentoId = tipoDocumentoId;
    }
}
