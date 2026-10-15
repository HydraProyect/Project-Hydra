using CaeManager.Domain.Common;

namespace CaeManager.Domain.Proyectos;

/// <summary>
/// Un Tipo de Documento que un Proyecto de tipo <see cref="TipoProyecto.Evento"/> pide a quien entra en él.
/// Sin ciclo de vida propio, igual que <c>TipoDocumentoCentro</c>: quitarlo es una baja física. A diferencia
/// de <c>TipoDocumentoCentro</c>, todavía no interviene en el cálculo de cumplimiento ni en el bloqueo de
/// acceso: es el listado de lo que se pide en el evento.
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
