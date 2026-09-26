using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.AsignarRolACuenta;

/// <summary>
/// Asigna un rol a una cuenta del Tenant activo que entró por SSO y espera uno (la
/// pestaña «Pendientes» de /roles). P1-I2: antes lo hacía <c>Roles.razor.cs</c>
/// contra <c>UserManager</c>, con la autorización en la página.
///
/// <para>
/// <b>Quién</b>: solo un Administrador, por su rol efectivo (la página exige el
/// mismo rol). Administrador y Dirección CAE solo se conceden desde el Tenant de
/// origen de quien asigna (<see cref="RolesReservadosAlTenantDeOrigen"/>).
/// </para>
///
/// <para>
/// <b>Propiedad antes que búsqueda</b>: un Operador CAE externo delegado se ve desde
/// el Tenant propietario, pero su cuenta es de otra organización y su rol se
/// gobierna allí. Antes se recuperaba por Guid sin mirar el TenantId, y el Id de
/// un usuario de otro Tenant bastaba para cambiarle el rol; por eso la propiedad
/// se comprueba antes de leer la cuenta.
/// </para>
/// </summary>
public record AsignarRolACuentaCommand(Guid UsuarioId, string Rol) : ICommand<CuentaConRolAsignado>;

/// <summary>Lo que la pantalla necesita para avisar al titular por correo.</summary>
public record CuentaConRolAsignado(Guid UsuarioId, string NombreCompleto, string Email);

public class AsignarRolACuentaCommandHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual)
    : IRequestHandler<AsignarRolACuentaCommand, Result<CuentaConRolAsignado>>
{
    public async Task<Result<CuentaConRolAsignado>> Handle(AsignarRolACuentaCommand request, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerRolEfectivoAsync() != AutoridadSobreCuentas.Administrador)
            return Result.Fallo<CuentaConRolAsignado>(AutoridadSobreCuentas.SinAutoridad);

        // Que el <select> solo ofrezca roles válidos no impide enviar otra cosa: sin
        // esto, AddToRoleAsync aceptaría cualquier nombre que exista en AspNetRoles.
        if (!AutoridadSobreCuentas.RolesExistentes.Contains(request.Rol))
            return Result.Fallo<CuentaConRolAsignado>(AutoridadSobreCuentas.RolDesconocido);

        var rolAsignable = RolesReservadosAlTenantDeOrigen.Verificar(
            request.Rol, await currentUserService.ObtenerTenantOrigenIdAsync(), tenantActual.TenantId);
        if (rolAsignable.EsFallido)
            return Result.Fallo<CuentaConRolAsignado>(rolAsignable.Error);

        if (!await cuentas.EsPropiaDelTenantActualAsync(request.UsuarioId, cancellationToken))
            return Result.Fallo<CuentaConRolAsignado>(AutoridadSobreCuentas.NoEncontrado);

        var cuenta = await cuentas.ObtenerAsync(request.UsuarioId, cancellationToken);
        if (cuenta is null)
            return Result.Fallo<CuentaConRolAsignado>(AutoridadSobreCuentas.NoEncontrado);

        var resultado = await cuentas.AsignarRolAsync(request.UsuarioId, request.Rol, cancellationToken);
        return resultado.EsFallido
            ? Result.Fallo<CuentaConRolAsignado>(resultado.Error)
            : Result.Exito(new CuentaConRolAsignado(cuenta.Id, cuenta.NombreCompleto, cuenta.Email));
    }
}
