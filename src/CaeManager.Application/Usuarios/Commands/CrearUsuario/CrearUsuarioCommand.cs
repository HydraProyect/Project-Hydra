using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.CrearUsuario;

/// <summary>
/// Da de alta una cuenta del Tenant activo, sin contraseña, con su rol, y devuelve
/// el token de activación con el que su titular la establece (P1-I2: antes lo hacía
/// <c>Usuarios.razor.cs</c> contra <c>UserManager</c>, con la autorización en la página).
///
/// <para>
/// <b>Quién</b>: Administrador o Dirección CAE, por su rol efectivo
/// (<see cref="AutoridadSobreCuentas"/>). Administrador y Dirección CAE solo se
/// conceden desde el Tenant de origen de quien da el alta
/// (<see cref="RolesReservadosAlTenantDeOrigen"/>, decisión del 2026-09-23).
/// </para>
///
/// <para>
/// <b>Permiso sensible</b>: solo lo concede un Administrador, y solo a otro
/// Administrador (Codex, HO-099-01): Dirección CAE también da altas, y sin esta
/// regla podría crear un Administrador con el permiso ya concedido.
/// </para>
///
/// <para>
/// Si el rol no se puede asignar, la cuenta ya existe: no se deshace el alta, y
/// <see cref="UsuarioCreado.FalloAlAsignarRol"/> lo dice para que la pantalla no
/// lo anuncie como un alta completa (sin rol la cuenta no da acceso a nada).
/// </para>
/// </summary>
public record CrearUsuarioCommand(
    string Email,
    string NombreCompleto,
    string Rol,
    Guid? CoordinadorUsuarioId,
    Guid? ClienteId,
    bool PermisoConsultarAccesoDocumentosSensibles) : ICommand<UsuarioCreado>;

public record UsuarioCreado(Guid UsuarioId, string TokenActivacion, Error? FalloAlAsignarRol);

public class CrearUsuarioCommandHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual)
    : IRequestHandler<CrearUsuarioCommand, Result<UsuarioCreado>>
{
    public async Task<Result<UsuarioCreado>> Handle(CrearUsuarioCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return Result.Fallo<UsuarioCreado>(AutoridadSobreCuentas.SinAutoridad);

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.NombreCompleto))
            return Result.Fallo<UsuarioCreado>(Error.Crear("Usuarios.DatosObligatorios", "Correo y nombre son obligatorios."));

        if (!AutoridadSobreCuentas.RolesExistentes.Contains(request.Rol))
            return Result.Fallo<UsuarioCreado>(AutoridadSobreCuentas.RolDesconocido);

        if (request.Rol == AutoridadSobreCuentas.RolCliente && request.ClienteId is null)
            return Result.Fallo<UsuarioCreado>(AutoridadSobreCuentas.ClienteRequerido);

        // ApplicationUser no lo sella el interceptor de tenant: la cuenta nace en
        // el Context Workspace activo, con su TenantId explícito.
        if (tenantActual.TenantId is not { } tenantId)
            return Result.Fallo<UsuarioCreado>(Error.Crear(
                "Usuarios.SinOrganizacion", "No pudimos determinar tu organización. Vuelve a iniciar sesión."));

        var rolAsignable = RolesReservadosAlTenantDeOrigen.Verificar(
            request.Rol, await currentUserService.ObtenerTenantOrigenIdAsync(), tenantId);
        if (rolAsignable.EsFallido)
            return Result.Fallo<UsuarioCreado>(rolAsignable.Error);

        var actorEsAdministrador = await currentUserService.ObtenerRolEfectivoAsync() == AutoridadSobreCuentas.Administrador;

        var creada = await cuentas.CrearAsync(new NuevaCuentaUsuario(
            request.Email,
            request.NombreCompleto,
            tenantId,
            request.Rol == AutoridadSobreCuentas.RolGestorCae ? request.CoordinadorUsuarioId : null,
            request.Rol == AutoridadSobreCuentas.RolCliente ? request.ClienteId : null,
            actorEsAdministrador
                && request.Rol == AutoridadSobreCuentas.Administrador
                && request.PermisoConsultarAccesoDocumentosSensibles), cancellationToken);
        if (creada.EsFallido)
            return Result.Fallo<UsuarioCreado>(creada.Error);

        var rol = await cuentas.AsignarRolAsync(creada.Valor, request.Rol, cancellationToken);

        var token = await cuentas.GenerarTokenActivacionAsync(creada.Valor, cancellationToken);
        if (token.EsFallido)
            return Result.Fallo<UsuarioCreado>(token.Error);

        return Result.Exito(new UsuarioCreado(creada.Valor, token.Valor, rol.EsFallido ? rol.Error : null));
    }
}
