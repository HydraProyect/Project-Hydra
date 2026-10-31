using CaeManager.Application.Comercial.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comercial.Commands.CambiarAddOnSubidaAutomatica;

/// <summary>
/// Activa o desactiva el Add-on de subida automática para un Tenant beneficiario. Plano comercial: lo
/// hace un Actor de Plataforma TALVEG con autorización sobre ese Tenant, igual que
/// <c>ActualizarEstadoComercialTenantCommand</c>, nunca el propio Tenant. El precio y la unidad de
/// consumo del Add-on siguen sin decidir, y este Command no depende de ellos.
/// </summary>
public record CambiarAddOnSubidaAutomaticaCommand(Guid TenantId, bool Activo) : ICommand;

public class CambiarAddOnSubidaAutomaticaCommandValidator : AbstractValidator<CambiarAddOnSubidaAutomaticaCommand>
{
    public CambiarAddOnSubidaAutomaticaCommandValidator()
    {
        RuleFor(c => c.TenantId).NotEmpty();
    }
}

public class CambiarAddOnSubidaAutomaticaCommandHandler(
    ITenantsQueryContext dbContext,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CambiarAddOnSubidaAutomaticaCommand, Result>
{
    public async Task<Result> Handle(CambiarAddOnSubidaAutomaticaCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Comercial.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        if (!await autorizacion.PuedeSobreTenantAsync(usuarioId.Value, request.TenantId, cancellationToken))
            return Result.Fallo(Error.Crear("Comercial.NoAutorizado", "No tienes autorización de plataforma sobre esta organización."));

        var tenant = await dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);
        if (tenant is null)
            return Result.Fallo(Error.Crear("Comercial.TenantNoEncontrado", "No encontramos esta organización."));

        if (!tenant.EsSuscribible())
            return Result.Fallo(Error.Crear("Comercial.TenantPlataforma", "La organización de la plataforma no contrata este servicio."));

        tenant.CambiarAddOnSubidaAutomatica(request.Activo);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
