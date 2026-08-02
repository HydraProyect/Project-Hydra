using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerActividadSoporte;

/// <summary>
/// Todo lo que hizo soporte en una visita concreta, en orden. Es lo que
/// permite responder a un cliente que pregunta qué se tocó en sus datos.
///
/// Lo consultan las dos partes, y por caminos distintos:
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
/// </summary>
public record ObtenerActividadSoporteQuery(Guid DelegacionTenantId) : IRequest<IReadOnlyList<ActividadSoporteDto>>;

public record ActividadSoporteDto(
    Guid Id, Guid UsuarioSoporteId, TipoActividadSoporte Tipo, string? Detalle, DateTime OcurridaEnUtc);

public class ObtenerActividadSoporteQueryHandler(
    ITenantsQueryContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerActividadSoporteQuery, IReadOnlyList<ActividadSoporteDto>>
{
    public async Task<IReadOnlyList<ActividadSoporteDto>> Handle(
        ObtenerActividadSoporteQuery request, CancellationToken cancellationToken)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (tenantOrigenId is null) return [];

        // DelegacionTenant es catálogo global (sin filtro), así que se lee
        // igual desde cualquier tenant — de ahí que la autorización se
        // compruebe a mano justo después.
        var delegacion = await dbContext.DelegacionesTenant
            .Where(d => d.Id == request.DelegacionTenantId && d.Proposito == PropositoDelegacion.Soporte)
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
            .Where(r => r.DelegacionTenantId == request.DelegacionTenantId)
            .OrderByDescending(r => r.OcurridaEnUtc)
            .Select(r => new ActividadSoporteDto(r.Id, r.UsuarioSoporteId, r.Tipo, r.Detalle, r.OcurridaEnUtc))
            .ToListAsync(cancellationToken);
    }
}
