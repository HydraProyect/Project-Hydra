using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor;

/// <summary>
/// Restablece la verificación en dos pasos de <b>otra</b> cuenta del mismo Tenant
/// propietario: la desactiva, borra su clave de autenticador y sus códigos de
/// recuperación y cierra sus sesiones (P0-8 del plan de madurez 09-24, hallazgo
/// FS-01). Es la salida para quien perdió el móvil y los códigos: sin ella, el
/// único Administrador de un Tenant en esa situación dejaba al Tenant sin
/// administración y la única vía era la base de datos.
///
/// <para>
/// <b>Quién puede</b> lo decide <see cref="IAutorizacionRestablecerSegundoFactor"/>;
/// hoy, solo un Administrador del Tenant propietario sobre otra cuenta de su mismo
/// Tenant (<see cref="AutorizacionRestablecerSegundoFactorAdministrador"/>). Este
/// handler es el acto: autorizar, comprobar que la cuenta tiene la 2FA activa y
/// restablecer. Soporte TALVEG no llega: <c>AutorizacionEscrituraBehavior</c> solo
/// deja pasar a una Sesión Privilegiada comandos de aprovisionamiento, y este no lo es.
/// </para>
///
/// <para>
/// <b>Auditoría</b>: la escribe <c>AuditoriaInterceptor</c> sobre la cuenta afectada
/// (<c>EntidadId</c> = Usuario afectado, <c>TwoFactorEnabled</c> de true a false) con el
/// Actor real en <c>UsuarioId</c>/<c>ActorRealUsuarioId</c>, y una fila por cada token
/// borrado con su valor enmascarado. La separación entre Actor real y Usuario afectado
/// la da la fila misma; nada de eso se escribe a mano desde aquí.
/// </para>
/// </summary>
public record RestablecerSegundoFactorCommand(Guid UsuarioId) : ICommand;

public class RestablecerSegundoFactorCommandValidator : AbstractValidator<RestablecerSegundoFactorCommand>
{
    public RestablecerSegundoFactorCommandValidator()
    {
        RuleFor(c => c.UsuarioId).NotEmpty();
    }
}

public class RestablecerSegundoFactorCommandHandler(
    IAutorizacionRestablecerSegundoFactor autorizacion,
    ISegundoFactorDeCuentas segundoFactor)
    : IRequestHandler<RestablecerSegundoFactorCommand, Result>
{
    // Un único mensaje para "no existe" y "es de otro Tenant": uno distinto
    // revelaría que ese Id es una cuenta de otra organización.
    internal static readonly Error CuentaNoEncontrada =
        Error.Crear("SegundoFactor.CuentaNoEncontrada", "No encontramos esa cuenta en tu organización.");

    public async Task<Result> Handle(RestablecerSegundoFactorCommand request, CancellationToken cancellationToken)
    {
        var autorizado = await autorizacion.AutorizarAsync(request.UsuarioId, cancellationToken);
        if (autorizado.EsFallido)
            return autorizado;

        var estado = await segundoFactor.ObtenerEstadoAsync(request.UsuarioId, cancellationToken);
        if (estado is null)
            return Result.Fallo(CuentaNoEncontrada);

        if (!estado.Activo)
            return Result.Fallo(Error.Crear(
                "SegundoFactor.NoActivo", "Esa cuenta no tiene activada la verificación en dos pasos."));

        return await segundoFactor.RestablecerAsync(request.UsuarioId, cancellationToken);
    }
}
