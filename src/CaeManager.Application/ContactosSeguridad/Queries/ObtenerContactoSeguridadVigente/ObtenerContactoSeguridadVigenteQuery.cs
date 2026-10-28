using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using MediatR;

namespace CaeManager.Application.ContactosSeguridad.Queries.ObtenerContactoSeguridadVigente;

/// <summary>
/// El contacto de seguridad Validado de un Tenant, o null si no tiene. Lo leen dos actores, y nadie más
/// (falla cerrado: null):
/// <list type="bullet">
/// <item>el Administrador del Tenant, leído en la base, sobre su propio Tenant actual;</item>
/// <item>un Actor de Plataforma TALVEG con concesión AdminPlataforma global, sobre cualquier Tenant.</item>
/// </list>
/// </summary>
public record ObtenerContactoSeguridadVigenteQuery(Guid TenantId) : IRequest<ContactoSeguridadDto?>;

public class ObtenerContactoSeguridadVigenteQueryHandler(
    ITenantActual tenantActual,
    ICurrentUserService currentUserService,
    IAdministradorDelTenantPropietario administradorDelTenant,
    IAutorizacionAdminPlataforma autorizacion,
    IContactoSeguridadTenantRepository repositorio,
    IContactosSeguridadPlataforma plataforma)
    : IRequestHandler<ObtenerContactoSeguridadVigenteQuery, ContactoSeguridadDto?>
{
    public async Task<ContactoSeguridadDto?> Handle(
        ObtenerContactoSeguridadVigenteQuery request, CancellationToken cancellationToken)
    {
        if (request.TenantId == Guid.Empty
            || await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return null;

        if (tenantActual.TenantId == request.TenantId
            && await administradorDelTenant.EsAdministradorEnBaseAsync(usuarioId, request.TenantId, cancellationToken))
        {
            var vigente = (await repositorio.ObtenerPendienteYVigenteAsync(cancellationToken))
                .SingleOrDefault(c => c.Estado == EstadoContactoSeguridad.Validado);
            return vigente is null ? null : MapeoContactoSeguridad.ADto(vigente);
        }

        if (await autorizacion.PuedeGlobalmenteAsync(usuarioId, cancellationToken))
            return await plataforma.ObtenerVigenteAsync(request.TenantId, cancellationToken);

        return null;
    }
}
