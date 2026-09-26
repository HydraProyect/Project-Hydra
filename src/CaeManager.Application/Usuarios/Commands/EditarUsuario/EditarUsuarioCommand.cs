using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.EditarUsuario;

/// <summary>
/// Edita una cuenta del Tenant activo: nombre, rol, Coordinador CAE, Cliente
/// vinculado y permiso sensible (P1-I2: antes lo hacía <c>Usuarios.razor.cs</c>
/// contra <c>UserManager</c>, con la autorización en la página).
///
/// <para>
/// <b>Propiedad, no visibilidad</b>: un Operador CAE externo delegado se ve en la
/// lista del Tenant propietario, pero su cuenta se gobierna en su organización.
/// Para él la respuesta es la misma que «no encontrado», para no revelar con un
/// mensaje distinto que el Id es de otra organización.
/// </para>
///
/// <para>
/// <b>Rol</b>: la regla de <see cref="RolesReservadosAlTenantDeOrigen"/> solo se
/// aplica si el guardado <b>concede</b> el rol: conservar el que la cuenta ya tenía
/// no es concederlo, y editar el nombre de un Administrador existente no debe
/// bloquearse. Se decide antes de escribir nada.
/// </para>
///
/// <para>
/// <b>Permiso sensible</b> (Codex, HO-099-01 y revisión 2026-09-12):
/// <list type="bullet">
/// <item>si el rol resultante no es Administrador, se retira siempre, lo decida
/// quien lo decida: es consecuencia de perder el rol que la política exige;</item>
/// <item>si quien edita es Administrador, se aplica lo pedido salvo que sea
/// autogestión (<see cref="AutoridadSobreCuentas.EsAutogestionDelPermisoSensible"/>);</item>
/// <item>si quien edita no es Administrador y la cuenta ya lo era, se conserva el
/// valor guardado; si la está promoviendo, nace en <c>false</c>: nadie lo concede.</item>
/// </list>
/// </para>
///
/// <para>
/// Los datos y el rol son escrituras distintas de Identity. Si el rol falla, los
/// datos ya se guardaron, y el error dice en qué estado quedó el rol.
/// </para>
/// </summary>
public record EditarUsuarioCommand(
    Guid UsuarioId,
    string NombreCompleto,
    string Rol,
    Guid? CoordinadorUsuarioId,
    Guid? ClienteId,
    bool PermisoConsultarAccesoDocumentosSensibles) : ICommand;

public class EditarUsuarioCommandHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual)
    : IRequestHandler<EditarUsuarioCommand, Result>
{
    public static readonly Error AutogestionPermisoSensible = Error.Crear(
        "Usuarios.AutogestionPermisoSensible",
        "No puedes conceder ni revocar tu propio permiso de rastro de acceso a documentos sensibles. " +
        "Da de alta a otro Administrador y pídele que lo gestione.");

    public async Task<Result> Handle(EditarUsuarioCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return Result.Fallo(AutoridadSobreCuentas.SinAutoridad);

        if (string.IsNullOrWhiteSpace(request.NombreCompleto))
            return Result.Fallo(Error.Crear("Usuarios.NombreObligatorio", "El nombre es obligatorio."));

        if (!AutoridadSobreCuentas.RolesExistentes.Contains(request.Rol))
            return Result.Fallo(AutoridadSobreCuentas.RolDesconocido);

        if (request.Rol == AutoridadSobreCuentas.RolCliente && request.ClienteId is null)
            return Result.Fallo(AutoridadSobreCuentas.ClienteRequerido);

        var cuenta = await cuentas.ObtenerAsync(request.UsuarioId, cancellationToken);
        if (cuenta is null || !cuenta.EsPropiaDelTenantActual)
            return Result.Fallo(AutoridadSobreCuentas.NoEncontrado);

        var concedeRol = !cuenta.Roles.Contains(request.Rol);
        if (concedeRol)
        {
            var rolAsignable = RolesReservadosAlTenantDeOrigen.Verificar(
                request.Rol, await currentUserService.ObtenerTenantOrigenIdAsync(), tenantActual.TenantId);
            if (rolAsignable.EsFallido)
                return rolAsignable;
        }

        var actorEsAdministrador = await currentUserService.ObtenerRolEfectivoAsync() == AutoridadSobreCuentas.Administrador;
        var eraAdministrador = cuenta.Roles.Contains(AutoridadSobreCuentas.Administrador);

        bool permiso;
        if (request.Rol != AutoridadSobreCuentas.Administrador)
        {
            permiso = false;
        }
        else if (actorEsAdministrador)
        {
            if (AutoridadSobreCuentas.EsAutogestionDelPermisoSensible(
                    request.UsuarioId, await currentUserService.ObtenerUsuarioActualIdAsync(), request.Rol,
                    request.PermisoConsultarAccesoDocumentosSensibles, cuenta.PermisoConsultarAccesoDocumentosSensibles))
                return Result.Fallo(AutogestionPermisoSensible);

            permiso = request.PermisoConsultarAccesoDocumentosSensibles;
        }
        else
        {
            permiso = eraAdministrador && cuenta.PermisoConsultarAccesoDocumentosSensibles;
        }

        var datos = await cuentas.ActualizarDatosAsync(request.UsuarioId, new DatosCuentaUsuario(
            request.NombreCompleto,
            request.Rol == AutoridadSobreCuentas.RolGestorCae ? request.CoordinadorUsuarioId : null,
            request.Rol == AutoridadSobreCuentas.RolCliente ? request.ClienteId : null,
            permiso), cancellationToken);
        if (datos.EsFallido)
            return datos.Error.Codigo == AutoridadSobreCuentas.NoEncontrado.Codigo
                ? datos
                : Result.Fallo(Error.Crear(datos.Error.Codigo, $"No pudimos guardar los cambios. {datos.Error.Mensaje}"));

        if (!concedeRol)
            return Result.Exito();

        var cambio = await cuentas.CambiarRolAsync(request.UsuarioId, request.Rol, cancellationToken);
        return cambio.Desenlace switch
        {
            DesenlaceCambioRol.Cambiado => Result.Exito(),
            DesenlaceCambioRol.NoEncontrada => Result.Fallo(AutoridadSobreCuentas.NoEncontrado),
            DesenlaceCambioRol.FalloAlQuitarConservaElAnterior => Result.Fallo(Error.Crear(
                "Usuarios.RolConservado",
                $"Los datos se guardaron, pero no pudimos cambiar el rol. {cambio.MotivoIdentity} El usuario conserva su rol anterior.")),
            _ => Result.Fallo(Error.Crear(
                "Usuarios.SinRol",
                $"Los datos se guardaron, pero el cambio de rol quedó a medias. {cambio.MotivoIdentity} " +
                "El usuario se quedó sin ningún rol asignado: revísalo y asígnaselo a mano.")),
        };
    }
}
