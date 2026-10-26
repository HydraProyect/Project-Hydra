using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerOperadoresCaeExternos;

/// <summary>
/// Los Operadores CAE externos (Tenants con <see cref="Tenant.PuedeActuarComoOperadorCaeExterno"/>
/// concedida, nunca el Tenant de plataforma) con los Tenants propietarios que operan hoy. Alimenta
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
            .Where(t => t.PuedeActuarComoOperadorCaeExterno && !t.EsPlataforma)
            .OrderBy(t => t.Nombre)
            .Select(t => new { t.Id, t.Nombre, t.CreadoEnUtc })
            .ToListAsync(cancellationToken);

        if (operadores.Count == 0) return [];

        // DelegacionesTenant tiene RLS por sus dos extremos y el Actor de Plataforma no es ninguno
        // de ellos: tras la autorización de arriba, cada Operador se lee situándose en su propio
        // Tenant, igual que ObtenerActividadSoporteQuery se sitúa en el Tenant visitado.
        var resultado = new List<OperadorCaeExternoDto>(operadores.Count);
        foreach (var operador in operadores)
        {
            List<TenantPropietarioOperadoDto> operados;
            using (AmbitoTenantExplicito.Establecer(operador.Id))
            {
                operados = await (
                    from vinculo in dbContext.DelegacionesTenant
                    join propietario in dbContext.Tenants on vinculo.TenantClienteId equals propietario.Id
                    where vinculo.Activa
                          && vinculo.Proposito == PropositoDelegacion.OperadorExterno
                          && vinculo.TenantConsultoraId == operador.Id
                    orderby propietario.Nombre
                    select new TenantPropietarioOperadoDto(propietario.Id, propietario.Nombre))
                    .ToListAsync(cancellationToken);
            }

            resultado.Add(new OperadorCaeExternoDto(operador.Id, operador.Nombre, operador.CreadoEnUtc, operados));
        }

        return resultado;
    }
}
