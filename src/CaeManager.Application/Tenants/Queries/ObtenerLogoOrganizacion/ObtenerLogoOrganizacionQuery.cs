using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Logo;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerLogoOrganizacion;

/// <param name="TenantId">La organización cuyo logo se ve: la actual, o el objetivo de la sesión de soporte.</param>
/// <param name="LogoVersion">Hash corto del PNG servido; null si no hay logo. Solo rompe la caché de la URL.</param>
/// <param name="PuedeEditar">
/// Lo decide <see cref="IAutorizacionLogoTenant"/>, la misma barrera que aplican los comandos: la pantalla
/// solo la usa para ofrecer o esconder los botones, nunca para autorizar.
/// </param>
/// <param name="EnSesionDeSoporte">Distingue el motivo del solo lectura para explicarlo en pantalla.</param>
public sealed record LogoOrganizacionDto(
    Guid TenantId, string Nombre, string? LogoVersion, bool PuedeEditar, bool EnSesionDeSoporte);

/// <summary>
/// Datos de la pantalla Configuración → Organización (selector de Tenant beneficiario, lote 1). Solo lee
/// la fila de la organización actual, que un usuario autenticado ya conoce (su nombre está en la
/// cabecera): el alcance de otras organizaciones lo decide <c>ObtenerLogoTenantQuery</c>, no esta.
/// </summary>
public record ObtenerLogoOrganizacionQuery : IRequest<LogoOrganizacionDto?>;

public class ObtenerLogoOrganizacionQueryHandler(
    ITenantActual tenantActual,
    ITenantsQueryContext tenants,
    IAutorizacionLogoTenant autorizacion,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual)
    : IRequestHandler<ObtenerLogoOrganizacionQuery, LogoOrganizacionDto?>
{
    public async Task<LogoOrganizacionDto?> Handle(
        ObtenerLogoOrganizacionQuery request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId)
            return null;

        var fila = await tenants.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Nombre, t.LogoVersion })
            .FirstOrDefaultAsync(cancellationToken);
        if (fila is null)
            return null;

        return new LogoOrganizacionDto(
            tenantId,
            fila.Nombre,
            fila.LogoVersion,
            await autorizacion.PuedeEscribirAsync(tenantId, cancellationToken),
            await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null);
    }
}
