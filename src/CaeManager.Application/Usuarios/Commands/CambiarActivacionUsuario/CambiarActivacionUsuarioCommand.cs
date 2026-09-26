using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario;

/// <summary>
/// Desactiva o reactiva una cuenta del Tenant activo (P1-I2: antes lo hacía
/// <c>Usuarios.razor.cs</c> contra <c>UserManager</c>). Desactivar bloquea la
/// cuenta y renueva su sello de seguridad en la misma escritura, para que la
/// cookie y el circuito ya abiertos dejen de valer.
///
/// <para>
/// Nadie cambia la activación de su propia cuenta. Una cuenta que ya no existe y
/// una que existe pero es de otra organización (la fila de un Operador CAE externo
/// delegado) dan errores distintos: la primera pide recargar la lista; la
/// segunda es una fila legítima que no se puede tocar desde aquí.
/// </para>
/// </summary>
public record CambiarActivacionUsuarioCommand(Guid UsuarioId, bool Activar) : ICommand;

public class CambiarActivacionUsuarioCommandHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService)
    : IRequestHandler<CambiarActivacionUsuarioCommand, Result>
{
    public async Task<Result> Handle(CambiarActivacionUsuarioCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return Result.Fallo(AutoridadSobreCuentas.SinAutoridad);

        if (await currentUserService.ObtenerUsuarioActualIdAsync() == request.UsuarioId)
            return Result.Fallo(Error.Crear("Usuarios.PropiaCuenta", "No puedes desactivar tu propia cuenta."));

        var cuenta = await cuentas.ObtenerAsync(request.UsuarioId, cancellationToken);
        if (cuenta is null)
            return Result.Fallo(AutoridadSobreCuentas.CuentaInexistente);
        if (!cuenta.EsPropiaDelTenantActual)
            return Result.Fallo(AutoridadSobreCuentas.NoEncontrado);

        var resultado = await cuentas.CambiarActivacionAsync(request.UsuarioId, request.Activar, cancellationToken);
        if (resultado.EsExitoso) return resultado;

        return resultado.Error.Codigo == AutoridadSobreCuentas.NoEncontrado.Codigo
            ? Result.Fallo(AutoridadSobreCuentas.CuentaInexistente)
            : Result.Fallo(Error.Crear(
                resultado.Error.Codigo,
                $"No pudimos {(request.Activar ? "reactivar" : "desactivar")} esta cuenta. {resultado.Error.Mensaje}"));
    }
}
