using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;

/// <summary>
/// Retira a un Operador Delegado concreto de un Delegated Workspace, sin
/// revocar la delegación entera — el caso corriente de una persona que deja
/// la Consultora o cambia de cartera. Sin esto, la única forma de quitarle el
/// acceso era revocar la delegación completa, que afecta a todo el equipo
/// (hallazgo N-4 de INFORME-AUDITORIA-2.md).
/// </summary>
public record RevocarAsignacionOperadorDelegadoCommand(Guid AsignacionId) : ICommand;

public class RevocarAsignacionOperadorDelegadoCommandValidator : AbstractValidator<RevocarAsignacionOperadorDelegadoCommand>
{
    public RevocarAsignacionOperadorDelegadoCommandValidator()
    {
        RuleFor(c => c.AsignacionId).NotEmpty().WithMessage("Indica el operador que quieres retirar.");
    }
}

public class RevocarAsignacionOperadorDelegadoCommandHandler(
    IAsignacionOperadorDelegadoRepository repositorio,
    IDelegacionTenantRepository delegacionRepositorio,
    ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RevocarAsignacionOperadorDelegadoCommand, Result>
{
    public async Task<Result> Handle(RevocarAsignacionOperadorDelegadoCommand request, CancellationToken cancellationToken)
    {
        var asignacion = await repositorio.ObtenerPorIdAsync(request.AsignacionId, cancellationToken);
        if (asignacion is null)
            return Result.Fallo(Error.Crear("AsignacionOperadorDelegado.NoEncontrada", "No encontramos esa asignación."));

        // La asignación no dice por sí sola de qué dos tenants se trata: la
        // autorización cuelga de su delegación.
        var delegacion = await delegacionRepositorio.ObtenerPorIdAsync(asignacion.DelegacionTenantId, cancellationToken);

        if (delegacion is null ||
            !await DesactivarDelegacionTenantCommandHandler.DelegacionAdministrablePorElUsuarioAsync(delegacion, currentUserService))
            return Result.Fallo(Error.Crear("AsignacionOperadorDelegado.NoEncontrada", "No encontramos esa asignación."));

        repositorio.Eliminar(asignacion);

        // La fila antigua se borra; la cartera se CIERRA. La diferencia es
        // deliberada: el modelo nuevo conserva el rastro de quién operó qué y
        // hasta cuándo, y borrarla perdería exactamente eso.
        if (delegacion.Proposito == PropositoDelegacion.Comercial)
            await asignacionesWriter.CerrarCarteraOperadorAsync(
                delegacion.TenantClienteId, delegacion.TenantConsultoraId,
                asignacion.UsuarioId, MotivoCierreAsignacion.Revocada, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
