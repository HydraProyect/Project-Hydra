using CaeManager.Application.Common;
using CaeManager.Application.Usuarios.Commands.GenerarActivacionUsuario;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.CorregirCorreoCuentaPendiente;

/// <summary>
/// Corrige el correo de una cuenta del Tenant activo que sigue pendiente de
/// activación —el caso de un alta con el correo mal escrito— y devuelve un token de
/// activación nuevo para enviarlo a la dirección corregida.
///
/// <para>
/// Es un control de seguridad además de una comodidad: el enlace de activación es una
/// credencial al portador y el que se envió fue a parar al correo equivocado. Por eso
/// corregir el correo deja sin valor todos los enlaces emitidos hasta entonces, en la
/// misma escritura que cambia la dirección, y queda en la auditoría de la cuenta con
/// el correo anterior y el nuevo
/// (<see cref="IGestionCuentasUsuario.CorregirCorreoPendienteAsync"/>).
/// </para>
///
/// <para>
/// <b>Solo mientras la cuenta está pendiente.</b> Si ya se activó, alguien usó el
/// enlace: la operación falla y lo dice, y no cambia nada. Cambiar el correo de una
/// cuenta activada es otra operación, que hoy no existe, y no pasa por aquí.
/// </para>
///
/// <para>
/// <b>Quién puede</b>: quien puede crear esa cuenta y reenviarle la activación
/// (<see cref="AutoridadSobreCuentas.PuedeGestionarCuentasAsync"/>, sobre una cuenta
/// propia del Tenant activo). No amplía autoridad.
/// </para>
/// </summary>
public record CorregirCorreoCuentaPendienteCommand(Guid UsuarioId, string CorreoNuevo) : ICommand<string>;

public class CorregirCorreoCuentaPendienteCommandHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual)
    : IRequestHandler<CorregirCorreoCuentaPendienteCommand, Result<string>>
{
    public static readonly Error CorreoObligatorio = Error.Crear(
        "Usuarios.CorreoObligatorio", "Escribe el correo correcto de esta persona.");

    public static readonly Error MismoCorreo = Error.Crear(
        "Usuarios.MismoCorreo",
        "Ese ya es el correo de esta cuenta. Si solo quieres enviarle otro enlace, usa «Reenviar correo de activación».");

    public async Task<Result<string>> Handle(CorregirCorreoCuentaPendienteCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return Result.Fallo<string>(AutoridadSobreCuentas.SinAutoridad);

        var correoNuevo = request.CorreoNuevo?.Trim();
        if (string.IsNullOrEmpty(correoNuevo))
            return Result.Fallo<string>(CorreoObligatorio);

        var cuenta = await cuentas.ObtenerAsync(request.UsuarioId, cancellationToken);
        if (cuenta is null)
            return Result.Fallo<string>(AutoridadSobreCuentas.CuentaInexistente);
        if (!cuenta.EsPropiaDelTenantActual)
            return Result.Fallo<string>(AutoridadSobreCuentas.NoEncontrado);

        // Misma regla que el reenvío: corregirle el correo a un Administrador pendiente
        // equivale a quedarse con su cuenta —el enlace nuevo sale hacia la dirección que
        // elige quien lo pide—, así que una cuenta con rol de Propiedad solo la toca quien
        // actúa en su propio Tenant de origen (acto excluido del Encargo de administración, D-8).
        var destinoIntocable = CuentasConRolDePropiedad.VerificarDestino(
            cuenta.Roles, await currentUserService.ObtenerTenantOrigenIdAsync(), tenantActual.TenantId);
        if (destinoIntocable.EsFallido)
            return Result.Fallo<string>(destinoIntocable.Error);

        if (!cuenta.PendienteActivacion)
            return Result.Fallo<string>(GenerarActivacionUsuarioCommandHandler.YaActivada);

        if (string.Equals(cuenta.Email, correoNuevo, StringComparison.OrdinalIgnoreCase))
            return Result.Fallo<string>(MismoCorreo);

        var token = await cuentas.CorregirCorreoPendienteAsync(request.UsuarioId, correoNuevo, cancellationToken);

        // Se activó entre la comprobación de arriba y la escritura: mismo desenlace.
        return token.EsFallido && token.Error.Codigo == AutoridadSobreCuentas.YaNoPendiente.Codigo
            ? Result.Fallo<string>(GenerarActivacionUsuarioCommandHandler.YaActivada)
            : token;
    }
}
