using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Logo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Tenants.Commands.RetirarLogoTenant;

/// <summary>
/// Retira el logo del Tenant actual; a partir de ahí se pintan sus iniciales. Mismo autorizador y
/// mismo marcador que <c>GuardarLogoTenantCommand</c>. Idempotente: sin logo, no hace nada.
/// </summary>
public record RetirarLogoTenantCommand : ICommand, IComandoDeAprovisionamiento;

public class RetirarLogoTenantCommandHandler(
    ITenantActual tenantActual,
    IAutorizacionLogoTenant autorizacion,
    ITenantRepository tenantRepository,
    IFileStorageService almacenamiento,
    IUnitOfWork unitOfWork,
    ILogger<RetirarLogoTenantCommandHandler> logger)
    : IRequestHandler<RetirarLogoTenantCommand, Result>
{
    public async Task<Result> Handle(RetirarLogoTenantCommand request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId
            || !await autorizacion.PuedeEscribirAsync(tenantId, cancellationToken))
            return Result.Fallo(ErroresLogoTenant.NoAutorizado);

        var tenant = await tenantRepository.ObtenerPorIdAsync(tenantId, cancellationToken);
        if (tenant is null)
            return Result.Fallo(ErroresLogoTenant.NoAutorizado);

        if (tenant.LogoArchivoClave is not { } claveAnterior)
            return Result.Exito();

        tenant.RetirarLogo(DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Primero la columna, después el blob: si el borrado falla, queda un huérfano que nadie
        // referencia, nunca una columna apuntando a un blob inexistente.
        try
        {
            await almacenamiento.EliminarAsync(claveAnterior, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo borrar el blob {Archivo} del logo del Tenant {TenantId}.", claveAnterior, tenantId);
        }

        return Result.Exito();
    }
}
