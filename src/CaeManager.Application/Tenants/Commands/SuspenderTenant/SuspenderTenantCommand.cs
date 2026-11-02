using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Commands.SuspenderTenant;

/// <summary>
/// Suspensión operativa de un Tenant por incidente o seguridad (decisión D-3, opción c): corte total
/// de su acceso, no el corte por impago, que es <see cref="EstadoComercialTenant"/> y lo trae Stripe.
/// Lo hace un Actor de Plataforma TALVEG con autorización sobre ese Tenant, nunca el propio Tenant, y
/// el Tenant de plataforma no se suspende: dejaría sin Actor a quien tendría que reactivarlo.
/// Suspender uno ya suspendido no hace nada: no mueve el sello de las claves de API.
/// </summary>
public record SuspenderTenantCommand(Guid TenantId) : ICommand;

public class SuspenderTenantCommandValidator : AbstractValidator<SuspenderTenantCommand>
{
    public SuspenderTenantCommandValidator()
    {
        RuleFor(c => c.TenantId).NotEmpty();
    }
}

public class SuspenderTenantCommandHandler(
    ITenantsQueryContext dbContext,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    ITransaccionDeComando transaccion,
    IBloqueoEstadoOperativoTenant bloqueo,
    IDescarteCambiosPendientes descarte,
    IUnitOfWork unitOfWork)
    : IRequestHandler<SuspenderTenantCommand, Result>
{
    public async Task<Result> Handle(SuspenderTenantCommand request, CancellationToken cancellationToken)
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

            if (!tenant.EsSuscribible())
                return Result.Fallo(Error.Crear("Tenant.Plataforma", "La organización de la plataforma no se puede suspender."));

            if (tenant.Estado is EstadoTenant.Suspendido)
                return Result.Exito();

            tenant.Suspender();
            await unitOfWork.SaveChangesAsync(ct);

            return Result.Exito();
        }, cancellationToken);
    }
}
