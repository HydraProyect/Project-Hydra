using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.CapacidadesTenant.Commands.RevocarCapacidadTenant;

/// <summary>
/// El Administrador del Tenant propietario revoca la concesión vigente de una <see cref="CapacidadTenant"/>
/// (decisión D-3 del 2026-11-15). La fila no se borra: queda con quién la revocó y cuándo, auditada con la acción
/// <c>CapacidadRevocada</c>. Desde el guardado, <c>AutoridadCapacidadesTenant</c> deja de reconocerla.
/// </summary>
[AreaDelEncargo("CapacidadesTenant")]
public record RevocarCapacidadTenantCommand(Guid UsuarioId, CapacidadTenant Capacidad) : ICommand;

public class RevocarCapacidadTenantCommandValidator : AbstractValidator<RevocarCapacidadTenantCommand>
{
    public RevocarCapacidadTenantCommandValidator()
    {
        RuleFor(c => c.UsuarioId).NotEmpty().WithMessage("Indica la cuenta a la que se revoca la capacidad.");
        RuleFor(c => c.Capacidad).IsInEnum().WithMessage("Capacidad desconocida.");
    }
}

public class RevocarCapacidadTenantCommandHandler(
    IAutoridadCapacidadesTenant autoridad,
    IActorAuditoria actorAuditoria,
    IConcesionCapacidadTenantRepository concesiones,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RevocarCapacidadTenantCommand, Result>
{
    public async Task<Result> Handle(RevocarCapacidadTenantCommand request, CancellationToken cancellationToken)
    {
        if (!await autoridad.EsAdministradorDelTenantPropietarioAsync(cancellationToken)
            || (await actorAuditoria.ObtenerAsync()).ActorRealUsuarioId is not { } actorRealId)
            return Result.Fallo(ErroresCapacidadesTenant.SoloAdministradorDelTenant);

        var concesion = await concesiones.ObtenerVigenteAsync(request.UsuarioId, request.Capacidad, cancellationToken);
        if (concesion is null)
            return Result.Fallo(ErroresCapacidadesTenant.NoVigente);

        concesion.Revocar(actorRealId, DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
