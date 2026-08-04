using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>Fake en memoria — los handlers/servicios de Application se prueban sin base de datos (ver CODING_STANDARDS.md).</summary>
public class ContactoWhatsAppRepositorioFalso : IContactoWhatsAppRepository
{
    public List<ContactoWhatsApp> Contactos { get; } = [];

    public Task<ContactoWhatsApp?> ObtenerPorTelefonoAsync(string telefono, CancellationToken cancellationToken = default) =>
        Task.FromResult(Contactos.FirstOrDefault(c => c.Telefono == telefono));

    public void Agregar(ContactoWhatsApp contacto) => Contactos.Add(contacto);
}
