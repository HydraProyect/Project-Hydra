namespace CaeManager.Domain.Comunicaciones;

public interface IContactoWhatsAppRepository
{
    /// <summary>Leg 1 del enrutamiento híbrido de la ingesta: ¿este teléfono ya se resolvió alguna vez contra un Cliente?</summary>
    Task<ContactoWhatsApp?> ObtenerPorTelefonoAsync(string telefono, CancellationToken cancellationToken = default);

    void Agregar(ContactoWhatsApp contacto);
}
