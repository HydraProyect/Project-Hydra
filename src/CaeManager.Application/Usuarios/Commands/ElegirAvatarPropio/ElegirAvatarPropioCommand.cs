using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.ElegirAvatarPropio;

/// <summary>
/// Elige el avatar de la <b>propia</b> cuenta entre los de <see cref="CatalogoAvatares"/>,
/// o lo quita (<paramref name="Clave"/> <c>null</c>) para volver a las iniciales. Nunca es
/// obligatorio (decisión de producto del 2026-10-08).
///
/// <para>
/// Autoservicio (<see cref="IComandoDeAutoservicio"/>): la cuenta sale de
/// <see cref="ICurrentUserService"/>, nunca del request, y el avatar es de la identidad,
/// no de un Tenant: se elige una vez y se ve igual en todos los Tenants que la cuenta
/// opere. Por eso lo puede ejecutar también un rol de solo lectura. Quien simula a otro
/// usuario no le cambia el avatar: el actor real tiene que ser la propia cuenta.
/// </para>
/// </summary>
public record ElegirAvatarPropioCommand(string? Clave) : ICommand, IComandoDeAutoservicio;

public class ElegirAvatarPropioCommandHandler(
    ICurrentUserService currentUserService,
    IActorAuditoria actorAuditoria,
    IAvatarDeCuentas avatares)
    : IRequestHandler<ElegirAvatarPropioCommand, Result>
{
    public async Task<Result> Handle(ElegirAvatarPropioCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Avatar.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.UsuarioSimuladoId is not null || actor.ActorRealUsuarioId != usuarioId)
            return Result.Fallo(Error.Crear(
                "Avatar.SoloLaPropiaCuenta", "El avatar solo lo puede cambiar la persona titular de la cuenta."));

        string? clave = null;
        if (!string.IsNullOrEmpty(request.Clave))
        {
            // Se guarda la clave canónica del catálogo, no el texto que llegó.
            clave = CatalogoAvatares.Resolver(request.Clave)?.Clave;
            if (clave is null)
                return Result.Fallo(Error.Crear("Avatar.NoEstaEnElCatalogo", "Ese avatar no está en el catálogo. Elige otro."));
        }

        return await avatares.GuardarAsync(usuarioId.Value, clave, cancellationToken);
    }
}
