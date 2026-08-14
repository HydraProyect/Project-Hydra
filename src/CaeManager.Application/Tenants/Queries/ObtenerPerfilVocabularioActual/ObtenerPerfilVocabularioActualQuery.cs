using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;

/// <summary>
/// Perfil de vocabulario (DDL-072) del tenant <b>activo</b> — a propósito
/// <see cref="ITenantActual"/> y no el tenant de origen: en un Delegated
/// Workspace, la interfaz debe hablar el idioma del tenant cuyos datos se
/// están mirando, no el del operador. Capa de presentación pura: nada aquí
/// cambia una consulta de dominio, solo qué etiqueta usa la pantalla.
/// </summary>
public record ObtenerPerfilVocabularioActualQuery : IRequest<PerfilVocabularioTenant>;

public class ObtenerPerfilVocabularioActualQueryHandler(
    ITenantsQueryContext tenantsContext, ITenantActual tenantActual, IVistaVocabularioPreviewService vistaPreview)
    : IRequestHandler<ObtenerPerfilVocabularioActualQuery, PerfilVocabularioTenant>
{
    public async Task<PerfilVocabularioTenant> Handle(ObtenerPerfilVocabularioActualQuery request, CancellationToken cancellationToken)
    {
        // Vista previa de Administrador (ver IVistaVocabularioPreviewService):
        // gana siempre que esté activa, sin tocar el perfil real del tenant.
        if (vistaPreview.PerfilForzado is { } perfilForzado) return perfilForzado;

        if (tenantActual.TenantId is null) return PerfilVocabularioTenant.ClienteDirecto;

        return await tenantsContext.Tenants
            .Where(t => t.Id == tenantActual.TenantId.Value)
            .Select(t => t.PerfilVocabulario)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
