using CaeManager.Domain.Contactos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ContactoAgendaRepository(CaeManagerDbContext dbContext) : IContactoAgendaRepository
{
    public void Agregar(ContactoAgenda contacto) => dbContext.ContactosAgenda.Add(contacto);

    /// <summary>
    /// Borrado real, no soft delete: un contacto retirado de la agenda no es
    /// histórico que preservar —a diferencia de una ReclamacionDocumental, que
    /// sí registra "qué se pidió y a quién"—, y dejarlo con EstaEliminado
    /// obligaría a filtrarlo en cada consulta de destinatarios.
    /// </summary>
    public void Eliminar(ContactoAgenda contacto) => dbContext.ContactosAgenda.Remove(contacto);

    public Task<ContactoAgenda?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ContactosAgenda
            .Include(c => c.TiposDocumento)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
}
