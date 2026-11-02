using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Commands.ReactivarTenant;

/// <summary>
/// Levanta la suspensión operativa de un Tenant (<c>SuspenderTenantCommand</c>). Las claves de API
/// anteriores no vuelven: quedan fuera por <see cref="Tenant.ClavesApiValidasDesdeUtc"/>. Reactivar
/// uno activo no hace nada.
/// </summary>
public record ReactivarTenantCommand(Guid TenantId) : ICommand;

public class ReactivarTenantCommandValidator : AbstractValidator<ReactivarTenantCommand>
{
    public ReactivarTenantCommandValidator()
    {
        RuleFor(c => c.TenantId).NotEmpty();
    }
}

public class ReactivarTenantCommandHandler(
    ITenantsQueryContext dbContext,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    ITransaccionDeComando transaccion,
    IBloqueoEstadoOperativoTenant bloqueo,
    IDescarteCambiosPendientes descarte,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ReactivarTenantCommand, Result>
{
    public async Task<Result> Handle(ReactivarTenantCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Tenant.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeSobreTenantAsync(usuarioId.Value, request.TenantId, cancellationToken))
            return Result.Fallo(Error.Crear("Tenant.NoAutorizado", "No tienes autorización de plataforma sobre esta organización."));

        return await transaccion.EjecutarAsync(async ct =>
        {
            // Candado antes de leer y contexto limpio: el del circuito puede conservar el Tenant como
            // estaba cuando lo cargó, y decidir con esa copia daría por hecha una transición que no lo está.
            await bloqueo.BloquearAsync(request.TenantId, ct);
            descarte.DescartarCambiosPendientes();

            var tenant = await dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == request.TenantId, ct);
            if (tenant is null)
                return Result.Fallo(Error.Crear("Tenant.NoEncontrado", "No encontramos esta organización."));

            if (tenant.Estado is EstadoTenant.Activo)
                return Result.Exito();

            tenant.Reactivar();
            await unitOfWork.SaveChangesAsync(ct);

            return Result.Exito();
        }, cancellationToken);
    }
}
