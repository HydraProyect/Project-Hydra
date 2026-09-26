using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.EliminarUsuarioPendiente;

/// <summary>
/// Borra una cuenta del Tenant activo que sigue <b>pendiente de activación</b>
/// (P1-I2: antes lo hacía <c>Usuarios.razor.cs</c> contra <c>UserManager</c>). Sin
/// contraseña ni login externo nunca hubo sesión, así que no hay historial de la
/// persona que perder. Una cuenta ya activada no se borra desde aquí: qué pasa con
/// lo que creó o le asignaron es otra decisión de producto, y para eso existe
/// desactivar.
///
/// <para>
/// Tampoco se borra una cuenta pendiente con una Asignación de Cartera vigente
/// (revisión de Codex): <c>AsignacionCartera.UsuarioId</c> no lleva FK hacia la
/// cuenta, y la baja la dejaría apuntando a un Id sin cuenta resoluble.
/// </para>
/// </summary>
public record EliminarUsuarioPendienteCommand(Guid UsuarioId) : ICommand;

public class EliminarUsuarioPendienteCommandHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarUsuarioPendienteCommand, Result>
{
    public static readonly Error NoPendiente = Error.Crear(
        "Usuarios.NoPendiente",
        "Esta cuenta ya tiene contraseña o inicia sesión por SSO; no se puede eliminar desde aquí.");

    public static readonly Error CarteraVigente = Error.Crear(
        "Usuarios.CarteraVigente",
        "Esta persona ya tiene una Asignación de Cartera vigente; reasígnala o retírala antes de eliminar la cuenta.");

    public async Task<Result> Handle(EliminarUsuarioPendienteCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return Result.Fallo(AutoridadSobreCuentas.SinAutoridad);

        if (await currentUserService.ObtenerUsuarioActualIdAsync() == request.UsuarioId)
            return Result.Fallo(Error.Crear("Usuarios.PropiaCuenta", "No puedes eliminar tu propia cuenta."));

        var cuenta = await cuentas.ObtenerAsync(request.UsuarioId, cancellationToken);
        if (cuenta is null || !cuenta.EsPropiaDelTenantActual)
            return Result.Fallo(AutoridadSobreCuentas.NoEncontrado);

        if (!cuenta.PendienteActivacion)
            return Result.Fallo(NoPendiente);

        if (await cuentas.TieneVinculoOperativoAsync(request.UsuarioId, cancellationToken))
            return Result.Fallo(CarteraVigente);

        var resultado = await cuentas.EliminarAsync(request.UsuarioId, cancellationToken);
        if (resultado.EsExitoso || resultado.Error.Codigo == AutoridadSobreCuentas.NoEncontrado.Codigo)
            return resultado;

        return Result.Fallo(Error.Crear(resultado.Error.Codigo, $"No pudimos eliminar esta cuenta. {resultado.Error.Mensaje}"));
    }
}
