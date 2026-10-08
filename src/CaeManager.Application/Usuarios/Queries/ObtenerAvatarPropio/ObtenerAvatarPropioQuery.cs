using CaeManager.Application.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio;

/// <summary>
/// La clave del avatar de quien mira, o <c>null</c> si no eligió ninguno (o no hay
/// sesión). La usan el menú de cuenta y la pantalla donde se elige. Devuelve la clave
/// tal cual está guardada: quien pinta la pasa por <see cref="CatalogoAvatares.Resolver"/>.
/// </summary>
public record ObtenerAvatarPropioQuery : IRequest<string?>;

public class ObtenerAvatarPropioQueryHandler(ICurrentUserService currentUserService, IAvatarDeCuentas avatares)
    : IRequestHandler<ObtenerAvatarPropioQuery, string?>
{
    public async Task<string?> Handle(ObtenerAvatarPropioQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        return usuarioId is null ? null : await avatares.ObtenerAsync(usuarioId.Value, cancellationToken);
    }
}
