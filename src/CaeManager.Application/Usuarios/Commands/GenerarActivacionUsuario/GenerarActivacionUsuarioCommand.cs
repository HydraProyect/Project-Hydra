using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.GenerarActivacionUsuario;

/// <summary>
/// Genera un token de activación nuevo para una cuenta del Tenant activo que sigue
/// pendiente de activación, para reenviarle el correo (P1-I2: antes lo hacía
/// <c>Usuarios.razor.cs</c> contra <c>UserManager</c>).
///
/// <para>
/// Se revalida contra la cuenta recién leída, no contra la fila con la que se
/// pulsó el menú: si la persona completó su activación entretanto, emitir el
/// enlace mostraría a quien administra un token de restablecimiento válido para
/// una cuenta que ya tiene contraseña. Para esa cuenta el canal es «olvidé mi
/// contraseña».
/// </para>
/// </summary>
public record GenerarActivacionUsuarioCommand(Guid UsuarioId) : ICommand<string>;

public class GenerarActivacionUsuarioCommandHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService)
    : IRequestHandler<GenerarActivacionUsuarioCommand, Result<string>>
{
    public static readonly Error YaActivada = Error.Crear(
        "Usuarios.YaActivada", "Esta cuenta ya no está pendiente de activación; recargamos la lista.");

    public async Task<Result<string>> Handle(GenerarActivacionUsuarioCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return Result.Fallo<string>(AutoridadSobreCuentas.SinAutoridad);

        var cuenta = await cuentas.ObtenerAsync(request.UsuarioId, cancellationToken);
        if (cuenta is null)
            return Result.Fallo<string>(Error.Crear("Usuarios.CuentaInexistente", "Esta cuenta ya no existe."));
        if (!cuenta.EsPropiaDelTenantActual)
            return Result.Fallo<string>(AutoridadSobreCuentas.NoEncontrado);

        if (!cuenta.PendienteActivacion)
            return Result.Fallo<string>(YaActivada);

        var token = await cuentas.GenerarTokenActivacionAsync(request.UsuarioId, cancellationToken);

        // Se activó entre la comprobación de arriba y la emisión: mismo desenlace.
        return token.EsFallido && token.Error.Codigo == AutoridadSobreCuentas.YaNoPendiente.Codigo
            ? Result.Fallo<string>(YaActivada)
            : token;
    }
}
