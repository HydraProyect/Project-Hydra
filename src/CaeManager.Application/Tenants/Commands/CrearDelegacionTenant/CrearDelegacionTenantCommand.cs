using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;

/// <summary>
/// Crea un Delegated Workspace: autoriza a la Consultora
/// <paramref name="TenantConsultoraId"/> a operar sobre el Cliente Delegante
/// <paramref name="TenantClienteId"/> (ADR-004 § 5.3). No concede acceso a
/// ningún usuario por sí sola — eso es <c>CrearAsignacionOperadorDelegadoCommand</c>.
/// </summary>
public record CrearDelegacionTenantCommand(Guid TenantConsultoraId, Guid TenantClienteId) : ICommand<Guid>;

public class CrearDelegacionTenantCommandValidator : AbstractValidator<CrearDelegacionTenantCommand>
{
    public CrearDelegacionTenantCommandValidator()
    {
        RuleFor(c => c.TenantConsultoraId).NotEmpty().WithMessage("Selecciona la Consultora.");
        RuleFor(c => c.TenantClienteId).NotEmpty().WithMessage("Selecciona el Cliente Delegante.");
        RuleFor(c => c)
            .Must(c => c.TenantConsultoraId != c.TenantClienteId)
            .WithMessage("Un tenant no puede delegarse acceso a sí mismo.");
    }
}

public class CrearDelegacionTenantCommandHandler(IDelegacionTenantRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearDelegacionTenantCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearDelegacionTenantCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteActivaAsync(request.TenantConsultoraId, request.TenantClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "DelegacionTenant.YaActiva", "Ya existe una delegación activa entre esta Consultora y este Cliente."));

        var delegacion = new DelegacionTenant(request.TenantConsultoraId, request.TenantClienteId);
        repositorio.Agregar(delegacion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(delegacion.Id);
    }
}
