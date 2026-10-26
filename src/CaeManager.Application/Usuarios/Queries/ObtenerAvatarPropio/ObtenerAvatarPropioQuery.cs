using CaeManager.Application.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio;

/// <summary>
/// La clave del avatar de quien mira, o <c>null</c> si no eligió ninguno (o no hay
/// sesión). La usan el menú de cuenta y la pantalla donde se elige. Devuelve la clave
/// tal cual está guardada: quien pinta la pasa por <see cref="CatalogoAvatares.Resolver"/>.
/// </summary>
public record ObtenerAvatarPropioQuery : IRequest<string?>;

public class ObtenerAvatarPropioQueryHandler(ICurrentUserService currentUserService, IAvatarDeCuentas avatares,
    ITenantActual? tenantActual = null, MemoAvatarPropio? memo = null)
    : IRequestHandler<ObtenerAvatarPropioQuery, string?>
{
    public async Task<string?> Handle(ObtenerAvatarPropioQuery request, CancellationToken cancellationToken)
    {
        if (memo is not null && tenantActual is not null
            && await ClaveContextoMemo.ResolverAsync(currentUserService, tenantActual) is { } clave)
            return await memo.ObtenerOCalcularAsync(clave, CalcularAsync, cancellationToken);

        return await CalcularAsync(cancellationToken);
    }

    private async Task<string?> CalcularAsync(CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        return usuarioId is null ? null : await avatares.ObtenerAsync(usuarioId.Value, cancellationToken);
    }
}

/// <summary>Lectura Scoped del avatar; cada Command invalida también esta preferencia.</summary>
public sealed class MemoAvatarPropio(TimeProvider? reloj = null) : MemoPorContexto<string?>(reloj);
