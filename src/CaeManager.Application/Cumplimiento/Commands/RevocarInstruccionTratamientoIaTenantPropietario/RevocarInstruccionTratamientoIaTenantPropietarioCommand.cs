using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Cumplimiento.Commands.RevocarInstruccionTratamientoIaTenantPropietario;

/// <summary>
/// Cierra la instrucción vigente de un Tenant propietario: a partir de aquí
/// el Nivel 0 (DEC-33) vuelve a bloquear el tratamiento con IA para ese
/// tenant hasta que se registre una nueva. No borra la fila — ver
/// <see cref="Domain.Cumplimiento.InstruccionTratamientoIaTenantPropietario.Revocar"/>.
/// </summary>
public record RevocarInstruccionTratamientoIaTenantPropietarioCommand(Guid TenantPropietarioId, string Motivo) : ICommand;

public class RevocarInstruccionTratamientoIaTenantPropietarioCommandValidator
    : AbstractValidator<RevocarInstruccionTratamientoIaTenantPropietarioCommand>
{
    public RevocarInstruccionTratamientoIaTenantPropietarioCommandValidator()
    {
        RuleFor(c => c.TenantPropietarioId).NotEmpty();

        RuleFor(c => c.Motivo)
            .NotEmpty().WithMessage("Indica por qué se retira la autorización de tratamiento con IA.")
            .MaximumLength(Domain.Cumplimiento.InstruccionTratamientoIaTenantPropietario.LongitudMaximaMotivoRevocacion);
    }
}

public class RevocarInstruccionTratamientoIaTenantPropietarioCommandHandler(
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IInstruccionTratamientoIaTenantPropietarioRepository repositorio,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RevocarInstruccionTratamientoIaTenantPropietarioCommand, Result>
{
    public async Task<Result> Handle(
        RevocarInstruccionTratamientoIaTenantPropietarioCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("InstruccionTratamientoIa.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeSobreTenantAsync(usuarioId.Value, request.TenantPropietarioId, cancellationToken))
            return Result.Fallo(Error.Crear(
                "InstruccionTratamientoIa.SinAutoridad",
                "No tienes capacidad de administración de plataforma sobre ese tenant."));

        // La lectura tiene que ir bajo el MISMO ámbito que la escritura:
        // InstruccionesTratamientoIaTenantPropietario lleva RLS + filtro
        // global de EF por TenantId, así que leerla antes de abrir el
        // ámbito explícito vería cero filas (filtrando por el tenant de
        // origen del administrador, no por el tenant objetivo) — ver el
        // mismo razonamiento en RegistrarInstruccionTratamientoIaTenantPropietarioCommand.
        using (AmbitoTenantExplicito.Establecer(request.TenantPropietarioId))
        {
            var vigente = await repositorio.ObtenerVigenteAsync(request.TenantPropietarioId, cancellationToken);
            if (vigente is null)
                return Result.Fallo(Error.Crear(
                    "InstruccionTratamientoIa.NoEncontrada", "Este tenant no tiene ninguna instrucción vigente que revocar."));

            vigente.Revocar(request.Motivo, DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Exito();
        }
    }
}
