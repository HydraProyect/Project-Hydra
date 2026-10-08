using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerEncargosAdministracion;

/// <summary>
/// Los Encargos de administración del Tenant activo, vigentes e históricos
/// (decisión D-8, 2026-10-08), para la pantalla que los registra y los retira.
///
/// <para>
/// Solo la ve quien puede gobernar el encargo
/// (<see cref="AutoridadSobreElEncargo"/>): un Administrador propio del Tenant
/// propietario o Soporte TALVEG en una Sesión Privilegiada de aprovisionamiento
/// sobre ese Tenant. A cualquier otro —incluido quien administra por encargo—
/// le devuelve una lista vacía: no distingue «no hay encargos» de «no puedes
/// verlos».
/// </para>
///
/// <para>
/// Quién registró y quién retiró viajan como Id: la cuenta puede ser de Soporte
/// TALVEG, que no está en el directorio del Tenant propietario.
/// </para>
/// </summary>
public record ObtenerEncargosAdministracionQuery : IRequest<IReadOnlyList<EncargoAdministracionDto>>;

public enum EstadoEncargoAdministracion
{
    Vigente = 1,
    Caducado = 2,
    Retirado = 3,
}

public record EncargoAdministracionDto(
    Guid Id,
    Guid AsignacionOperacionId,
    Guid OperadorTenantId,
    string OperadorNombre,
    string ClausulaContrato,
    string VersionTexto,
    OrigenEncargoAdministracion Origen,
    Guid RegistradoPorUsuarioId,
    DateTime RegistradoEnUtc,
    DateTime VigenciaDesde,
    DateTime? VigenciaHasta,
    Guid? RetiradoPorUsuarioId,
    DateTime? RetiradoEnUtc,
    EstadoEncargoAdministracion Estado);

public class ObtenerEncargosAdministracionQueryHandler(
    ITenantActual tenantActual,
    AutoridadSobreElEncargo autoridad,
    IEncargosAdministracionQueryContext encargosContext,
    ITenantsQueryContext tenantsContext,
    TimeProvider reloj)
    : IRequestHandler<ObtenerEncargosAdministracionQuery, IReadOnlyList<EncargoAdministracionDto>>
{
    public async Task<IReadOnlyList<EncargoAdministracionDto>> Handle(
        ObtenerEncargosAdministracionQuery request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } propietarioTenantId
            || await autoridad.ResolverAsync(propietarioTenantId, cancellationToken) is null)
            return [];

        var filas = await (
                from encargo in encargosContext.EncargosAdministracion.AsNoTracking()
                where encargo.PropietarioTenantId == propietarioTenantId
                join operador in tenantsContext.Tenants.AsNoTracking()
                    on encargo.OperadorTenantId equals operador.Id
                orderby encargo.RegistradoEnUtc descending
                select new { Encargo = encargo, OperadorNombre = operador.Nombre })
            .ToListAsync(cancellationToken);

        var ahora = reloj.GetUtcNow().UtcDateTime;
        return filas
            .Select(f => new EncargoAdministracionDto(
                f.Encargo.Id, f.Encargo.AsignacionOperacionId, f.Encargo.OperadorTenantId, f.OperadorNombre,
                f.Encargo.ClausulaContrato, f.Encargo.VersionTexto, f.Encargo.Origen,
                f.Encargo.RegistradoPorUsuarioId, f.Encargo.RegistradoEnUtc,
                f.Encargo.VigenciaDesde, f.Encargo.VigenciaHasta,
                f.Encargo.RetiradoPorUsuarioId, f.Encargo.RetiradoEnUtc,
                EstadoDe(f.Encargo, ahora)))
            .ToList();
    }

    private static EstadoEncargoAdministracion EstadoDe(EncargoAdministracion encargo, DateTime ahora) =>
        encargo.RetiradoEnUtc is not null ? EstadoEncargoAdministracion.Retirado
        : encargo.EstaVigente(ahora) ? EstadoEncargoAdministracion.Vigente
        : EstadoEncargoAdministracion.Caducado;
}
