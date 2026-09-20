using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerOperadoresCaeExternos;

/// <summary>
/// Los Operadores CAE externos (Tenants de perfil <c>Consultora</c>, nunca el
/// Tenant de plataforma) con los Tenants propietarios que operan hoy. Alimenta
/// el panel de alta del Actor de Plataforma en <c>/delegaciones</c>.
///
/// Es una lectura transversal a todos los Tenants: la autoridad es la misma
/// que la del alta (<see cref="IAutorizacionAdminPlataforma.PuedeGlobalmenteAsync"/>)
/// y, sin ella, devuelve una lista vacía — nunca un error que distinga «no hay
/// ninguno» de «no puedes verlos» por el mensaje. Solo cuenta delegaciones
/// <c>OperadorExterno</c> activas: las de Soporte son del plano de plataforma
/// (ADR-011 § 8), no operación.
/// </summary>
public record ObtenerOperadoresCaeExternosQuery : IRequest<IReadOnlyList<OperadorCaeExternoDto>>;

public record TenantPropietarioOperadoDto(Guid TenantId, string Nombre);

public record OperadorCaeExternoDto(
    Guid TenantId, string Nombre, DateTime CreadoEnUtc, IReadOnlyList<TenantPropietarioOperadoDto> TenantsPropietarios);

public class ObtenerOperadoresCaeExternosQueryHandler(
    ITenantsQueryContext dbContext,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerOperadoresCaeExternosQuery, IReadOnlyList<OperadorCaeExternoDto>>
{
    public async Task<IReadOnlyList<OperadorCaeExternoDto>> Handle(
        ObtenerOperadoresCaeExternosQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null || !await autorizacion.PuedeGlobalmenteAsync(usuarioId.Value, cancellationToken))
            return [];

        var operadores = await dbContext.Tenants
            .Where(t => t.PerfilVocabulario == PerfilVocabularioTenant.Consultora && !t.EsPlataforma)
            .OrderBy(t => t.Nombre)
            .Select(t => new { t.Id, t.Nombre, t.CreadoEnUtc })
            .ToListAsync(cancellationToken);

        if (operadores.Count == 0) return [];

        var operadorIds = operadores.Select(o => o.Id).ToList();

        var operados = await (
            from vinculo in dbContext.DelegacionesTenant
            join propietario in dbContext.Tenants on vinculo.TenantClienteId equals propietario.Id
            where vinculo.Activa
                  && vinculo.Proposito == PropositoDelegacion.OperadorExterno
                  && operadorIds.Contains(vinculo.TenantConsultoraId)
            orderby propietario.Nombre
            select new { vinculo.TenantConsultoraId, PropietarioId = propietario.Id, propietario.Nombre })
            .ToListAsync(cancellationToken);

        var porOperador = operados
            .GroupBy(x => x.TenantConsultoraId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TenantPropietarioOperadoDto>)g
                    .Select(x => new TenantPropietarioOperadoDto(x.PropietarioId, x.Nombre)).ToList());

        return operadores
            .Select(o => new OperadorCaeExternoDto(o.Id, o.Nombre, o.CreadoEnUtc, porOperador.GetValueOrDefault(o.Id, [])))
            .ToList();
    }
}
