using CaeManager.Domain.Contactos;

namespace CaeManager.Application.Contactos;

public interface IContactosAgendaQueryContext
{
    IQueryable<ContactoAgenda> ContactosAgenda { get; }
    IQueryable<ContactoAgendaTipoDocumento> ContactosAgendaTiposDocumento { get; }
    IQueryable<ContactoAgendaRol> ContactosAgendaRoles { get; }
}
