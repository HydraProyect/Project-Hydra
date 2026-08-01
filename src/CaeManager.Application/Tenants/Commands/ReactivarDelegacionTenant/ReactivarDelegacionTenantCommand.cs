using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;

/// <summary>
/// Vuelve a activar una delegación revocada, sin perder qué operadores tenía
/// asignados — es la contrapartida de <c>DesactivarDelegacionTenantCommand</c>
/// y la razón de que revocar sea <c>Activa = false</c> y no un borrado
/// (ADR-004 § 5.5).
/// </summary>
public record ReactivarDelegacionTenantCommand(Guid DelegacionTenantId) : ICommand;

public class ReactivarDelegacionTenantCommandValidator : AbstractValidator<ReactivarDelegacionTenantCommand>
{
    public ReactivarDelegacionTenantCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty().WithMessage("Indica la delegación que quieres reactivar.");
    }
}

public class ReactivarDelegacionTenantCommandHandler(
    IDelegacionTenantRepository repositorio, ICurrentUserService currentUserService, IUnitOfWork unitOfWork)
    : IRequestHandler<ReactivarDelegacionTenantCommand, Result>
{
    public async Task<Result> Handle(ReactivarDelegacionTenantCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await repositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);

        // Mismo criterio de autorización que revocar — ver ese handler.
        if (delegacion is null ||
            !await DesactivarDelegacionTenantCommandHandler.DelegacionAdministrablePorElUsuarioAsync(delegacion, currentUserService))
            return Result.Fallo(Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación."));

        if (delegacion.Activa)
            return Result.Fallo(Error.Crear("DelegacionTenant.YaActiva", "Esta delegación ya estaba activa."));

        delegacion.Reactivar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
