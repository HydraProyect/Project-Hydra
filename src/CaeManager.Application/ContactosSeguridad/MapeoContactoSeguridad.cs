using CaeManager.Domain.Tenants;

namespace CaeManager.Application.ContactosSeguridad;

internal static class MapeoContactoSeguridad
{
    public static ContactoSeguridadDto ADto(ContactoSeguridadTenant contacto) => new(
        contacto.Id, contacto.Nombre, contacto.Correo, contacto.Telefono,
        contacto.PropuestoEnUtc, contacto.ResueltoEnUtc, contacto.MotivoRechazo);
}
