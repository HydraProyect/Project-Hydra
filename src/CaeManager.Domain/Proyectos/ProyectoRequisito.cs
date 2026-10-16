using CaeManager.Domain.Common;

namespace CaeManager.Domain.Proyectos;

/// <summary>
/// Un Tipo de Documento que un Proyecto de tipo <see cref="TipoProyecto.Evento"/> pide a quien entra en él.
/// Sin ciclo de vida propio, igual que <c>TipoDocumentoCentro</c>: quitarlo es una baja física. Mientras el evento
/// aporta requisitos (<see cref="VigenciaEvento.AportaRequisitosEn"/>: desde la asignación, mientras es futuro o
/// vigente), cuenta para todos los Trabajadores asignados a él, voluntarios o no, incluidos los de Subcontratas: en los
/// avisos de faltantes, en el estado y el cumplimiento del Centro del evento y en el estado de la Subcontrata, salvo
/// que el Centro no tenga gestión CAE. Solo admite tipos de ámbito Trabajador. Todavía no interviene en el bloqueo de acceso.
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
