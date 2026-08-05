using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

public class ContactoWhatsAppRepository(CaeManagerDbContext dbContext) : IContactoWhatsAppRepository
{
    public Task<ContactoWhatsApp?> ObtenerPorTelefonoAsync(string telefono, CancellationToken cancellationToken = default) =>
        dbContext.ContactosWhatsApp.FirstOrDefaultAsync(c => c.Telefono == telefono, cancellationToken);

    public void Agregar(ContactoWhatsApp contacto) => dbContext.ContactosWhatsApp.Add(contacto);
}
