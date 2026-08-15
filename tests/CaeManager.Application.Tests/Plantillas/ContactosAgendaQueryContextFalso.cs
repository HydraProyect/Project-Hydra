using CaeManager.Application.Contactos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Contactos;

namespace CaeManager.Application.Tests.Plantillas;

public class ContactosAgendaQueryContextFalso : IContactosAgendaQueryContext
{
    public List<ContactoAgenda> ListaContactosAgenda { get; } = [];
    public List<ContactoAgendaTipoDocumento> ListaContactosAgendaTiposDocumento { get; } = [];
    public List<ContactoAgendaRol> ListaContactosAgendaRoles { get; } = [];

    public IQueryable<ContactoAgenda> ContactosAgenda => new TestAsyncQueryable<ContactoAgenda>(ListaContactosAgenda.AsQueryable());
    public IQueryable<ContactoAgendaTipoDocumento> ContactosAgendaTiposDocumento => new TestAsyncQueryable<ContactoAgendaTipoDocumento>(ListaContactosAgendaTiposDocumento.AsQueryable());
    public IQueryable<ContactoAgendaRol> ContactosAgendaRoles => new TestAsyncQueryable<ContactoAgendaRol>(ListaContactosAgendaRoles.AsQueryable());
}
