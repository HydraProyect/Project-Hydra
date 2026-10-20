using MediatR;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// Conteo de Trabajadores extranjeros cobrables de un mes natural por Tenant beneficiario: el servicio único que consume
/// la valoración del Operador CAE en el Cuadro de dirección (decisión de Chris, 2026-10-20). Sale de las mismas filas
/// que la exportación (<see cref="ObtenerExtranjerosDelMesQuery"/>), así que el número y el Excel no pueden divergir:
/// mismo criterio (la foto de la asignación, <c>VisitaTrabajador.EsExtranjeroCobrable</c>), misma autorización y mismo
/// alcance (solo Tenants que quien mira alcanza enteros).
/// </summary>
public record ContarExtranjerosCobrablesDelMesQuery(int Anyo, int Mes) : IRequest<ConteoExtranjerosCobrablesDelMesDto>;

/// <param name="Asignaciones">Filas de la exportación: asignaciones Trabajador–Visita cobrables del mes.</param>
/// <param name="TrabajadoresDistintos">Trabajadores extranjeros cobrables distintos del mes en ese Tenant.</param>
public record ConteoExtranjerosCobrablesTenantDto(Guid TenantId, string TenantNombre, int Asignaciones, int TrabajadoresDistintos);

/// <param name="PorTenant">Solo Tenants con alguna asignación cobrable en el mes.</param>
/// <param name="TenantsSinAlcanceCompleto">Como en <see cref="ExtranjerosDelMesDto"/>.</param>
/// <param name="SinCifras">Como en <see cref="ExtranjerosDelMesDto"/>.</param>
public record ConteoExtranjerosCobrablesDelMesDto(
    int Anyo, int Mes, IReadOnlyList<ConteoExtranjerosCobrablesTenantDto> PorTenant, int TenantsSinAlcanceCompleto,
    CuadroDireccionSinCifras? SinCifras = null);

public class ContarExtranjerosCobrablesDelMesQueryHandler(IMediator mediator)
    : IRequestHandler<ContarExtranjerosCobrablesDelMesQuery, ConteoExtranjerosCobrablesDelMesDto>
{
    public async Task<ConteoExtranjerosCobrablesDelMesDto> Handle(
        ContarExtranjerosCobrablesDelMesQuery request, CancellationToken cancellationToken)
    {
        var extranjeros = await mediator.Send(new ObtenerExtranjerosDelMesQuery(request.Anyo, request.Mes), cancellationToken);

        var porTenant = extranjeros.Filas
            .GroupBy(f => new { f.TenantId, f.TenantNombre })
            .Select(g => new ConteoExtranjerosCobrablesTenantDto(
                g.Key.TenantId, g.Key.TenantNombre, g.Count(), g.Select(f => f.TrabajadorId).Distinct().Count()))
            .OrderBy(t => t.TenantNombre, StringComparer.CurrentCulture)
            .ToList();

        return new ConteoExtranjerosCobrablesDelMesDto(
            extranjeros.Anyo, extranjeros.Mes, porTenant, extranjeros.TenantsSinAlcanceCompleto, extranjeros.SinCifras);
    }
}