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
}
