using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.CapacidadesTenant.Commands.ConcederCapacidadTenant;

/// <summary>
/// El Administrador del Tenant propietario concede una <see cref="CapacidadTenant"/> a una cuenta de su propio
/// Tenant (decisión D-3 del 2026-11-15). El beneficiario tiene que pertenecer a este Tenant (Tenant de origen
/// igual al actual) y estar activo con un rol de escritura: una cuenta de un Operador CAE externo no la recibe
/// nunca, y una de solo lectura no podría ejercerla. La concesión queda auditada como «Creado» con el actor real.
/// </summary>
[AreaDelEncargo("CapacidadesTenant")]
public record ConcederCapacidadTenantCommand(Guid UsuarioId, CapacidadTenant Capacidad) : ICommand;

public class ConcederCapacidadTenantCommandValidator : AbstractValidator<ConcederCapacidadTenantCommand>
{
    public ConcederCapacidadTenantCommandValidator()
    {
        RuleFor(c => c.UsuarioId).NotEmpty().WithMessage("Indica la cuenta que recibe la capacidad.");
        RuleFor(c => c.Capacidad).IsInEnum().WithMessage("Capacidad desconocida.");
    }
}

public class ConcederCapacidadTenantCommandHandler(
    IAutoridadCapacidadesTenant autoridad,
    ITenantActual tenantActual,
    IActorAuditoria actorAuditoria,
    IDirectorioUsuariosService directorioUsuarios,
    IConcesionCapacidadTenantRepository concesiones)
    : IRequestHandler<ConcederCapacidadTenantCommand, Result>
{
    public async Task<Result> Handle(ConcederCapacidadTenantCommand request, CancellationToken cancellationToken)
    {
        if (!await autoridad.EsAdministradorDelTenantPropietarioAsync(cancellationToken)
            || tenantActual.TenantId is not { } tenantId
            || (await actorAuditoria.ObtenerAsync()).ActorRealUsuarioId is not { } actorRealId)
            return Result.Fallo(ErroresCapacidadesTenant.SoloAdministradorDelTenant);

        if (!await EsBeneficiarioValidoAsync(request.UsuarioId, tenantId, cancellationToken))
            return Result.Fallo(ErroresCapacidadesTenant.BeneficiarioNoValido);

        if (await concesiones.TieneVigenteAsync(request.UsuarioId, request.Capacidad, cancellationToken))
            return Result.Fallo(ErroresCapacidadesTenant.YaConcedida);

        concesiones.Agregar(ConcesionCapacidadTenant.Conceder(
            request.UsuarioId, request.Capacidad, actorRealId, DateTime.UtcNow));
        return await concesiones.GuardarConcesionAsync(cancellationToken)
            ? Result.Exito()
            : Result.Fallo(ErroresCapacidadesTenant.YaConcedida);
    }

    private async Task<bool> EsBeneficiarioValidoAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken)
    {
        // El Tenant de la cuenta, no su visibilidad: EsVisibleEnTenantActualAsync daría por buena a una cuenta de un
        // Operador CAE externo que opera este Tenant.
        if (await directorioUsuarios.ObtenerTenantDeUsuarioAsync(usuarioId, cancellationToken) != tenantId)
            return false;

        foreach (var rol in AutorizacionEscrituraBehavior<ICommand, Result>.RolesConEscritura)
            if (await directorioUsuarios.EsCuentaActivaConRolAsync(usuarioId, tenantId, rol, cancellationToken))
                return true;
        return false;
    }
}
