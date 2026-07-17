namespace CaeManager.Application.Common;

/// <summary>
/// Abstrae la identidad del usuario autenticado frente a Infrastructure
/// (auditoría) y los handlers de Application. La implementación real vive en
/// Web, que es la única capa con acceso al contexto de autenticación de
/// Blazor Server.
/// </summary>
public interface ICurrentUserService
{
    Task<Guid?> ObtenerUsuarioActualIdAsync();

    /// <summary>
    /// Rol del usuario actual (el sistema asigna exactamente uno por
    /// usuario, ver Usuarios.razor). Null fuera de un circuito de Blazor
    /// (igual que ObtenerUsuarioActualIdAsync) o si el usuario no tiene
    /// ningún rol asignado.
    /// </summary>
    Task<string?> ObtenerRolActualAsync();
}
