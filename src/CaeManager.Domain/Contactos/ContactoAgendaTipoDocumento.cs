using CaeManager.Domain.Common;

namespace CaeManager.Domain.Contactos;

/// <summary>
/// "A esta persona se le pide este tipo de documento" — la fila que hace que
/// el RLC/TC1 vaya a Finanzas y los EPIs al responsable de PRL dentro del
/// mismo Cliente.
///
/// Entidad hija de <see cref="ContactoAgenda"/> (colección de solo lectura
/// sobre campo privado, mismo patrón que <c>ReclamacionDocumentalDocumento</c>).
/// </summary>
public class ContactoAgendaTipoDocumento : EntidadConTenant
{
    public Guid ContactoAgendaId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }

    private ContactoAgendaTipoDocumento()
    {
    }

    public ContactoAgendaTipoDocumento(Guid contactoAgendaId, Guid tipoDocumentoId)
    {
        ContactoAgendaId = contactoAgendaId;
        TipoDocumentoId = tipoDocumentoId;
    }
}
