using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerActividadSoporte;

/// <summary>
/// Todo lo que hizo soporte en una visita concreta, en orden. Es lo que
/// permite responder a un cliente que pregunta qué se tocó en sus datos.
///
/// Una visita cuelga de <b>exactamente uno</b> de los dos agrupadores de
/// <see cref="RegistroActividadSoporte"/>, y la consulta nombra uno solo
/// (<see cref="PorViaHeredada"/> o <see cref="PorSesionPrivilegiada"/>); con los
/// dos o con ninguno devuelve vacío.
///
/// <para>
/// <b>Por Delegación de Tenant de propósito Soporte</b> (vía heredada). La
/// consultan las dos partes, y por caminos distintos:
/// </para>
///
/// - El <b>cliente visitado</b> lo ve directamente: el registro lleva su
///   <c>TenantId</c>, así que el filtro global se lo entrega sin más.
/// - La <b>plataforma</b> necesita ámbito explícito, porque desde su propia
///   organización el filtro global esconde —correctamente— los registros del
///   cliente. Sin esto, quien hace el soporte no podría revisar ni justificar
///   su propia actuación.
///
/// Ese ámbito solo se abre tras comprobar que quien pregunta es el tenant de
/// plataforma y que la delegación es suya. Un tenant cualquiera no puede
/// mirar las visitas hechas a otro.
///
/// <para>
/// <b>Por Sesión Privilegiada</b> (issue #2016). Son las lecturas que Soporte
/// TALVEG hizo bajo una Sesión Privilegiada sobre el Tenant propietario (las
/// escribe <c>app_registrar_actividad_soporte_por_sesion</c>, issue #2001). Solo
/// las ve el <b>Administrador del Tenant propietario</b>, con el mismo predicado
/// que la sección «Accesos de Soporte TALVEG»
/// (<see cref="IAutorizacionDelegacionTenant"/>, contra su Tenant <b>de
/// origen</b>, nunca el Context Workspace), y solo las de su Tenant: el filtro
/// explícito por Tenant objetivo (<c>TenantId</c> del registro = Tenant de
/// origen) se suma a la RLS <c>aislamiento_tenant</c> y al filtro global. Sin
/// identidad del técnico (ADR-011 § 8.7, minimización): el DTO no lleva
/// <see cref="ActividadSoporteDto.UsuarioSoporteId"/> y la pantalla dice
/// «Soporte TALVEG». La RLS de <c>RegistrosActividadSoporte</c> aísla por Tenant,
/// no por rol (a diferencia de la política <c>administrador_del_tenant_objetivo</c>
/// de las Sesiones Privilegiadas): la comprobación de rol de este handler es la
/// única barrera de rol, no una redundancia.
/// </para>
/// </summary>
[AreaDelEncargo("Tenants")]
public record ObtenerActividadSoporteQuery(Guid? DelegacionTenantId, Guid? SesionPrivilegiadaId = null)
    : IRequest<IReadOnlyList<ActividadSoporteDto>>
{
    public static ObtenerActividadSoporteQuery PorViaHeredada(Guid delegacionTenantId) => new(delegacionTenantId);

    public static ObtenerActividadSoporteQuery PorSesionPrivilegiada(Guid sesionPrivilegiadaId) =>
        new(null, sesionPrivilegiadaId);
}

/// <param name="UsuarioSoporteId">
/// Quién, por la vía heredada. <c>null</c> por Sesión Privilegiada: el Tenant
/// propietario ve «Soporte TALVEG», no a la persona (ADR-011 § 8.7).
/// </param>
/// <param name="SesionPrivilegiadaId">La Sesión Privilegiada de la fila; <c>null</c> por la vía heredada.</param>
public record ActividadSoporteDto(
    Guid Id, Guid? UsuarioSoporteId, TipoActividadSoporte Tipo, string? Detalle, DateTime OcurridaEnUtc,
    Guid? SesionPrivilegiadaId = null);

public class ObtenerActividadSoporteQueryHandler(
    ITenantsQueryContext dbContext, ICurrentUserService currentUserService, IAutorizacionDelegacionTenant autorizacion)
    : IRequestHandler<ObtenerActividadSoporteQuery, IReadOnlyList<ActividadSoporteDto>>
{
    public async Task<IReadOnlyList<ActividadSoporteDto>> Handle(
        ObtenerActividadSoporteQuery request, CancellationToken cancellationToken)
    {
        var (agrupadorHeredado, agrupadorSesion) = request;
        return (agrupadorHeredado, agrupadorSesion) switch
        {
            ({ } delegacionTenantId, null) => await PorViaHeredadaAsync(delegacionTenantId, cancellationToken),
            (null, { } sesionPrivilegiadaId) => await PorSesionPrivilegiadaAsync(sesionPrivilegiadaId, cancellationToken),
            _ => [],
        };
    }

    private async Task<IReadOnlyList<ActividadSoporteDto>> PorViaHeredadaAsync(
        Guid delegacionTenantId, CancellationToken cancellationToken)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId is null) return [];

        // DelegacionTenant no tiene filtro de EF; su RLS la deja ver al cliente
        // visitado desde su Tenant y a la plataforma por su Tenant de origen.
        // La autorización se comprueba a mano justo después: RLS no distingue
        // el propósito ni quién de los dos extremos pregunta.
        var delegacion = await dbContext.DelegacionesTenant
            .Where(d => d.Id == delegacionTenantId && d.Proposito == PropositoDelegacion.Soporte)
            .Select(d => new { d.TenantConsultoraId, d.TenantClienteId })
            .FirstOrDefaultAsync(cancellationToken);

        if (delegacion is null) return [];

        var esElClienteVisitado = delegacion.TenantClienteId == tenantOrigenId.Value;

        var esLaPlataforma = delegacion.TenantConsultoraId == tenantOrigenId.Value && await dbContext.Tenants
            .AnyAsync(t => t.Id == tenantOrigenId.Value && t.EsPlataforma, cancellationToken);

        if (!esElClienteVisitado && !esLaPlataforma) return [];

        // El cliente lo ve por el filtro global; la plataforma necesita
        // situarse en el tenant visitado para que el filtro no lo esconda.
        using var ambito = esLaPlataforma
            ? AmbitoTenantExplicito.Establecer(delegacion.TenantClienteId)
            : null;

        return await dbContext.RegistrosActividadSoporte
            .Where(r => r.DelegacionTenantId == delegacionTenantId)
            .OrderByDescending(r => r.OcurridaEnUtc)
            .Select(r => new ActividadSoporteDto(r.Id, r.UsuarioSoporteId, r.Tipo, r.Detalle, r.OcurridaEnUtc, null))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ActividadSoporteDto>> PorSesionPrivilegiadaAsync(
        Guid sesionPrivilegiadaId, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (usuarioId is null || tenantOrigenId is null) return [];

        // Autoridad antes de leer: Administrador del Tenant de origen, resuelto
        // contra la base (mismo predicado que ObtenerAccesosSoporteTalvegQuery).
        if (!await autorizacion.PuedeGestionarDelegacionesAsync(
                usuarioId.Value, tenantOrigenId.Value, cancellationToken))
            return [];

        // Filtro explícito por Tenant objetivo, además de la RLS y del filtro
        // global: el Administrador del Tenant de plataforma pasa la comprobación
        // de arriba para SU Tenant, y sin este filtro un ámbito o un contexto
        // RLS de otro Tenant le entregaría lecturas ajenas.
        var tenantObjetivoId = tenantOrigenId.Value;
        return await dbContext.RegistrosActividadSoporte
            .Where(r => r.SesionPrivilegiadaId == sesionPrivilegiadaId && r.TenantId == tenantObjetivoId)
            .OrderByDescending(r => r.OcurridaEnUtc)
            .Select(r => new ActividadSoporteDto(
                r.Id, (Guid?)null, r.Tipo, r.Detalle, r.OcurridaEnUtc, r.SesionPrivilegiadaId))
            .ToListAsync(cancellationToken);
    }
}
