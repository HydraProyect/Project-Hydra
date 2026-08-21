using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

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

public class CrearDelegacionTenantCommandHandler(
    IDelegacionTenantRepository repositorio, ITenantsQueryContext tenantsContext,
    IAsignacionesOperativasWriter asignacionesWriter,
    IAutorizacionDelegacionTenant autorizacion, ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearDelegacionTenantCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearDelegacionTenantCommand request, CancellationToken cancellationToken)
    {
        // La autoridad va primero, antes incluso de comprobar que los tenants
        // existan: quien no puede gestionar estas delegaciones tampoco debería
        // poder averiguar, por la diferencia entre dos mensajes de error, qué
        // identificadores de tenant corresponden a organizaciones reales.
        //
        // Y la autoridad es del CLIENTE DELEGANTE, no de la Consultora ni de la
        // plataforma (ADR-004 § 12.2): quien concede acceso a unos datos es su
        // dueño. Que Hydra no pueda iniciar una delegación por su cuenta es § 11.1.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear(
                "DelegacionTenant.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeGestionarDelegacionesAsync(
                usuarioId.Value, request.TenantClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "DelegacionTenant.NoAutorizado",
                "Solo un administrador del Cliente Delegante puede autorizar el acceso a sus datos."));

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        // Tenant es catálogo global (Entity, no EntidadConTenant): la consulta
        // no lleva filtro de tenant a propósito, un Id de Tenant es válido
        // cross-tenant por diseño (ADR-004).
        if (!await tenantsContext.Tenants.AnyAsync(t => t.Id == request.TenantConsultoraId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("DelegacionTenant.ConsultoraNoEncontrada", "No encontramos esa Consultora."));

        if (!await tenantsContext.Tenants.AnyAsync(t => t.Id == request.TenantClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("DelegacionTenant.ClienteNoEncontrado", "No encontramos ese Cliente Delegante."));

        if (await repositorio.ExisteActivaAsync(request.TenantConsultoraId, request.TenantClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "DelegacionTenant.YaActiva", "Ya existe una delegación activa entre esta Consultora y este Cliente."));

        var delegacion = new DelegacionTenant(request.TenantConsultoraId, request.TenantClienteId);
        repositorio.Agregar(delegacion);

        // Doble escritura. El propietario de los datos es el Cliente Delegante
        // y el operador es la Consultora — el orden importa y es justo el que
        // encarna "delegación de acceso, no de propiedad".
        await asignacionesWriter.AbrirOperacionDelegadaAsync(
            request.TenantClienteId, request.TenantConsultoraId, delegacion.CreadoEnUtc, vigenciaHasta: null, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(delegacion.Id);
    }
}
