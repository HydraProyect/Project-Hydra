using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plataforma.Queries.ObtenerAccesosSoporteTalveg;

/// <summary>Cómo está hoy una Sesión Privilegiada vista desde el Tenant propietario.</summary>
public enum EstadoAccesoSoporteTalveg
{
    /// <summary>Abierta y dentro de su ventana: Soporte TALVEG puede estar leyendo ahora.</summary>
    Abierto,

    /// <summary>Cerrada antes de agotar la ventana.</summary>
    Cerrado,

    /// <summary>Nadie la cerró, pero su ventana ya pasó: no da acceso.</summary>
    Caducado,
}

/// <summary>
/// Una entrada de Soporte TALVEG en los datos del Tenant propietario. Sin
/// identidad del técnico ni capacidad: el Tenant propietario ve que fue
/// «Soporte TALVEG», por qué, con qué ticket y durante qué ventana (ADR-011
/// § 8.7, incremento 2). La trazabilidad de la persona concreta vive en el
/// plano de privilegio, que sigue sin ser legible desde el Tenant.
/// </summary>
public record AccesoSoporteTalvegDto(
    Guid SesionId,
    string Motivo,
    string? Ticket,
    DateTime InicioEnUtc,
    DateTime ExpiraEnUtc,
    DateTime? CerradaEnUtc,
    EstadoAccesoSoporteTalveg Estado);

/// <summary>
/// Las Sesiones Privilegiadas abiertas por Soporte TALVEG sobre el Tenant
/// propietario de la persona actual, de la más reciente a la más antigua.
/// <c>null</c> si no es Administrador de ese Tenant: la pantalla no muestra
/// la sección, en lugar de mostrarla vacía.
///
/// <para>
/// <b>Por qué esto no es el «listar sesiones» que <see cref="IPlataformaQueryContext"/>
/// prohíbe.</b> Aquel es un catálogo navegable del plano de privilegio; esto
/// es la otra posición que el propio contrato prevé, «el tenant visitado ve las
/// que le apuntan», acotada a UN Tenant objetivo: el de origen del
/// Administrador. Es la transparencia que compensa que la concesión de
/// SoporteLectura sea global (ADR-011 § 8.9).
/// </para>
///
/// <para>
/// <b>Dos barreras, en la misma frontera.</b> Aquí, el Tenant es el <b>de
/// origen</b> (nunca el Context Workspace: operar el workspace de otro Tenant no
/// da autoridad sobre él) y el rol se resuelve contra la base con el mismo
/// predicado que autoriza las delegaciones del Tenant propietario
/// (<see cref="IAutorizacionDelegacionTenant"/>). Debajo, la política RLS
/// <c>administrador_del_tenant_objetivo</c> solo entrega filas cuyo Tenant
/// objetivo es <c>app.tenant_id</c> y solo a un Administrador de ese Tenant:
/// si esta comprobación desapareciera, un usuario sin rol seguiría leyendo cero.
/// </para>
/// </summary>
public record ObtenerAccesosSoporteTalvegQuery : IRequest<IReadOnlyList<AccesoSoporteTalvegDto>?>;

public class ObtenerAccesosSoporteTalvegQueryHandler(
    ICurrentUserService currentUserService,
    IAutorizacionDelegacionTenant autorizacion,
    IPlataformaQueryContext plataformaContext)
    : IRequestHandler<ObtenerAccesosSoporteTalvegQuery, IReadOnlyList<AccesoSoporteTalvegDto>?>
{
    public async Task<IReadOnlyList<AccesoSoporteTalvegDto>?> Handle(
        ObtenerAccesosSoporteTalvegQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (usuarioId is null || tenantOrigenId is null) return null;

        if (!await autorizacion.PuedeGestionarDelegacionesAsync(
                usuarioId.Value, tenantOrigenId.Value, cancellationToken))
            return null;

        var tenantId = tenantOrigenId.Value;
        var sesiones = await plataformaContext.SesionesPrivilegiadas
            .Where(s => s.TenantObjetivoId == tenantId)
            .OrderByDescending(s => s.InicioEnUtc)
            .Select(s => new { s.Id, s.Motivo, s.Ticket, s.InicioEnUtc, s.ExpiraEnUtc, s.CerradaEnUtc })
            .ToListAsync(cancellationToken);

        var ahora = DateTime.UtcNow;
        return sesiones
            .Select(s => new AccesoSoporteTalvegDto(
                s.Id, s.Motivo, s.Ticket, s.InicioEnUtc, s.ExpiraEnUtc, s.CerradaEnUtc,
                s.CerradaEnUtc is not null ? EstadoAccesoSoporteTalveg.Cerrado
                : ahora < s.ExpiraEnUtc ? EstadoAccesoSoporteTalveg.Abierto
                : EstadoAccesoSoporteTalveg.Caducado))
            .ToList();
    }
}
