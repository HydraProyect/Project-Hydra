using CaeManager.Domain.Common;

namespace CaeManager.Domain.Contactos;

/// <summary>
/// "Esta persona desempeña este rol" — mismo patrón que
/// <see cref="ContactoAgendaTipoDocumento"/> (entidad hija de
/// <see cref="ContactoAgenda"/>, colección de solo lectura sobre campo
/// privado) y por el mismo motivo: un contacto puede tener varios roles a la
/// vez (ADR-010 § 2.5), así que es una tabla hija y no un campo único.
/// </summary>
public class ContactoAgendaRol : EntidadConTenant
{
    public Guid ContactoAgendaId { get; private set; }
    public RolContacto Rol { get; private set; }

    private ContactoAgendaRol()
    {
    }

    public ContactoAgendaRol(Guid contactoAgendaId, RolContacto rol)
    {
        ContactoAgendaId = contactoAgendaId;
        Rol = rol;
    }
}
