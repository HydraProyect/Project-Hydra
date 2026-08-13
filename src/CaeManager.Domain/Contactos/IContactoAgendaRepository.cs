namespace CaeManager.Domain.Contactos;

public interface IContactoAgendaRepository
{
    void Agregar(ContactoAgenda contacto);

    void Eliminar(ContactoAgenda contacto);

    /// <summary>Incluye <see cref="ContactoAgenda.TiposDocumento"/> — es lo que necesita la pantalla de edición y <c>EstablecerTiposDocumento</c>.</summary>
    Task<ContactoAgenda?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
}
