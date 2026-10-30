using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plataforma.Commands.RevocarConcesionPrivilegio;

/// <summary>
/// Cierra una <see cref="ConcesionPrivilegio"/> <see cref="EstadoConcesionPrivilegio.Vigente"/> antes
/// de que venza. Retirar autoridad es cerrarla, no borrarla: la fila sigue siendo el rastro de quién
/// tuvo autoridad y quién se la dio.
///
/// <para>
/// <b>Quién.</b> El Actor de Plataforma TALVEG nombrado como beneficiario o como concedente, ni uno
/// más: es la forma exacta de la política <c>privilegio_cierre_de_vigente</c>, que la base vuelve a
/// imponer al guardar (OWASP ASVS 4.0.3 V4.1.1). Ser concedente basta aunque su propia autoridad ya
/// haya vencido, igual que en la política. El titular puede renunciar a la suya.
/// </para>
///
/// <para>
/// <b>La marca es <see cref="ICommand"/>, la misma de <c>ConcederPrivilegioCommand</c>.</b>
/// <c>AutorizacionEscrituraBehavior</c> exige rol de escritura sin Sesión Privilegiada y, dentro de
/// una, lo rechaza con cualquier capacidad: ningún marcador de capacidad acotada lo admite, de modo
/// que administrar concesiones no se hace desde una inspección de soporte ni una impersonación. Tampoco
/// es <see cref="IComandoDeAutoservicio"/>: renunciar a la propia concesión escribe en el plano de
/// plataforma, no en las preferencias del usuario. Y, como al conceder, exige segundo factor activo
/// (V4.3.1): una cuenta con solo la contraseña no retira autoridad a nadie.
/// </para>
///
/// <para>
/// <b><see cref="CapacidadPrivilegio.AdminPlataforma"/> no se revoca por aquí (V4.1.3, V4.1.5).</b>
/// Hoy esa capacidad solo nace de <see cref="ConcesionPrivilegio.RaizDeBootstrap"/> y
/// <c>privilegio_autoconcesion</c> no admite otra vía, así que revocarla deja la plataforma sin
/// Actor con <c>AdminPlataforma</c> y con el bootstrap ya consumido, que es irreversible bajo
/// <c>cae_app_runtime</c>. El comando tampoco podría contar a los demás: <c>privilegio_del_usuario</c>
/// solo enseña las filas que te nombran. Mientras no se decida una regla de continuidad, se rechaza
/// siempre; la política de la base lo admite, y la restricción vive aquí.
/// </para>
/// </summary>
/// <param name="ConcesionPrivilegioId">La concesión que se cierra.</param>
public record RevocarConcesionPrivilegioCommand(Guid ConcesionPrivilegioId) : ICommand;

public class RevocarConcesionPrivilegioCommandValidator : AbstractValidator<RevocarConcesionPrivilegioCommand>
{
    public RevocarConcesionPrivilegioCommandValidator() => RuleFor(c => c.ConcesionPrivilegioId).NotEmpty();
}

public class RevocarConcesionPrivilegioCommandHandler(
    IPlataformaQueryContext plataformaContext,
    IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<RevocarConcesionPrivilegioCommand, Result>
{
    public async Task<Result> Handle(RevocarConcesionPrivilegioCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return NoPermitida();

        if (!await currentUserService.TieneDobleFactorActivoAsync())
            return Result.Fallo(Error.Crear(
                "ConcesionPrivilegio.SinDobleFactor",
                "Activa la autenticación en dos pasos en tu cuenta antes de revocar privilegios."));

        // Un Id ajeno y uno inexistente responden igual: bajo RLS, la fila que no te nombra ni se ve.
        var concesion = await plataformaContext.ConcesionesPrivilegio
            .FirstOrDefaultAsync(c => c.Id == request.ConcesionPrivilegioId, cancellationToken);
        if (concesion is null || (concesion.UsuarioPlataformaId != usuarioId.Value
                                  && concesion.ConcedidaPorUsuarioId != usuarioId.Value))
            return NoPermitida();

        if (concesion.Estado != EstadoConcesionPrivilegio.Vigente)
            return Result.Fallo(Error.Crear("ConcesionPrivilegio.YaCerrada", "La concesión de privilegio ya está cerrada."));

        if (concesion.Capacidad == CapacidadPrivilegio.AdminPlataforma)
            return Result.Fallo(Error.Crear(
                "ConcesionPrivilegio.AdminPlataformaNoRevocable",
                "La administración de la plataforma no se revoca desde aquí: la plataforma se quedaría sin administrador."));

        concesion.Revocar(DateTime.UtcNow);
        try
        {
            if (await unitOfWork.SaveChangesAsync(cancellationToken) == 0)
                return NoPermitida();
        }
        catch (DbUpdateConcurrencyException)
        {
            return NoPermitida();
        }

        return Result.Exito();
    }

    private static Result NoPermitida() => Result.Fallo(Error.Crear(
        "ConcesionPrivilegio.RevocacionNoPermitida",
        "No se pudo revocar la concesión de privilegio."));
}
