namespace CaeManager.Application.Common;

/// <summary>
/// Da a una operación su propio <c>CaeManagerDbContext</c> aislado, sin que
/// Application conozca EF Core (la implementación real vive en
/// Infrastructure — ver <c>PuertaAccesoDatos</c>, mismo patrón que
/// <see cref="IFileStorageService"/> o <see cref="IUnitOfWork"/>).
///
/// Todo camino que llegue a la base de datos desde un componente de Blazor
/// tiene que pasar por aquí: los despachos de MediatR entran solos (ver el
/// pipeline behavior que envuelve todo el pipeline), los accesos directos
/// (UserManager en páginas y layout, <c>DirectorioUsuariosTenant</c>,
/// <c>TrazaSoporteService</c>) se envuelven en su sitio. Reentrante por
/// flujo asíncrono: quien ya tiene un contexto activo lo reutiliza en vez de
/// crear uno nuevo — necesario para un handler que despacha otro request de
/// MediatR dentro de sí (ver <c>ObtenerKpisGlobalesQuery</c>, que además
/// cambia el tenant en vuelo con <c>AmbitoTenantExplicito</c> entre
/// iteraciones reutilizando el mismo contexto) o una página que envuelve su
/// carga entera y dentro despacha Queries.
/// </summary>
public interface IPuertaAccesoDatos
{
    Task<T> EjecutarAsync<T>(Func<Task<T>> operacion, CancellationToken cancellationToken = default);

    Task EjecutarAsync(Func<Task> operacion, CancellationToken cancellationToken = default);
}
