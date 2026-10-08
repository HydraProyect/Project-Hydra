using CaeManager.Domain.Common;

namespace CaeManager.Application.Usuarios;

/// <summary>
/// El avatar elegido de una cuenta de Identity, visto desde Application. Es un puerto
/// por el mismo motivo que <see cref="ISegundoFactorDeCuentas"/>: <c>ApplicationUser</c>
/// vive en Infrastructure.Identity. <b>No autoriza ni valida nada</b>: lee y escribe la
/// clave de la cuenta que le digan; que sea la propia y que la clave sea del catálogo
/// lo decide <c>ElegirAvatarPropioCommand</c>.
/// </summary>
public interface IAvatarDeCuentas
{
    /// <summary>La clave guardada, o <c>null</c> si la cuenta no eligió avatar o no existe.</summary>
    Task<string?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Guarda la clave; <c>null</c> quita el avatar y vuelven las iniciales.</summary>
    Task<Result> GuardarAsync(Guid usuarioId, string? clave, CancellationToken cancellationToken = default);
}
