using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using MediatR;

namespace CaeManager.Application.ContactosSeguridad.Queries.ObtenerContactoSeguridadDeMiOrganizacion;

/// <summary>
/// Lo que ve el Administrador del Tenant beneficiario en la sección «Contacto de seguridad» de su
/// organización: el contacto vigente, la propuesta pendiente y el último rechazo con su motivo. Solo para
/// el Administrador del Tenant actual, leído en la base; para cualquier otro, null y la sección no se pinta.
/// </summary>
public record ObtenerContactoSeguridadDeMiOrganizacionQuery : IRequest<ContactoSeguridadDeMiOrganizacionDto?>;

/// <param name="UltimoRechazo">El último rechazo, solo si es posterior a la última propuesta resuelta o
/// pendiente: un rechazo antiguo ya superado no se sigue mostrando.</param>
public record ContactoSeguridadDeMiOrganizacionDto(
    ContactoSeguridadDto? Vigente,
    ContactoSeguridadDto? Pendiente,
    ContactoSeguridadDto? UltimoRechazo);

public class ObtenerContactoSeguridadDeMiOrganizacionQueryHandler(
    ITenantActual tenantActual,
    ICurrentUserService currentUserService,
    IAdministradorDelTenantPropietario administradorDelTenant,
    IContactoSeguridadTenantRepository repositorio)
    : IRequestHandler<ObtenerContactoSeguridadDeMiOrganizacionQuery, ContactoSeguridadDeMiOrganizacionDto?>
{
    public async Task<ContactoSeguridadDeMiOrganizacionDto?> Handle(
        ObtenerContactoSeguridadDeMiOrganizacionQuery request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantId
            || await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId
            || !await administradorDelTenant.EsAdministradorEnBaseAsync(usuarioId, tenantId, cancellationToken))
            return null;

        var filas = await repositorio.ObtenerPendienteYVigenteAsync(cancellationToken);
        var vigente = filas.SingleOrDefault(c => c.Estado == EstadoContactoSeguridad.Validado);
        var pendiente = filas.SingleOrDefault(c => c.Estado == EstadoContactoSeguridad.Propuesto);
        var rechazo = await repositorio.ObtenerUltimoRechazadoAsync(cancellationToken);

        var referencia = pendiente?.PropuestoEnUtc ?? vigente?.ResueltoEnUtc;
        if (rechazo is not null && referencia is not null && rechazo.ResueltoEnUtc < referencia)
            rechazo = null;

        return new ContactoSeguridadDeMiOrganizacionDto(
            vigente is null ? null : MapeoContactoSeguridad.ADto(vigente),
            pendiente is null ? null : MapeoContactoSeguridad.ADto(pendiente),
            rechazo is null ? null : MapeoContactoSeguridad.ADto(rechazo));
    }
}
