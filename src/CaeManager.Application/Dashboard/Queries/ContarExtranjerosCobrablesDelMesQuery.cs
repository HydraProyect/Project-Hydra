using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// Conteo de Trabajadores extranjeros cobrables de un mes natural por Tenant beneficiario: el servicio único que consume
/// la valoración del Operador CAE en el Cuadro de dirección (decisión de Chris, 2026-10-20). Sale de las mismas filas
/// que la exportación (<see cref="ObtenerExtranjerosDelMesQuery"/>), así que el número y el Excel no pueden divergir:
/// mismo criterio (la foto de la asignación, <c>VisitaTrabajador.EsExtranjeroCobrable</c>), misma autorización y mismo
/// alcance (solo Tenants que quien mira alcanza enteros).
///
/// <para>
/// <b>Acotado a la vigencia</b>: con <paramref name="TramosPorTenant"/> (los trozos del mes en que la Asignación de Operación
/// externa del Operador CAE estuvo en vigor, <see cref="ValoracionOperadorCae.TramosEnElMes"/>) solo cuentan los Tenants
/// que figuran ahí y, de ellos, las asignaciones cuya Visita empieza en un día dentro de un tramo: el mismo borde que
/// <c>MedicionMensualCuadroDireccion.MedirUnidadesOperadorAsync</c> aplica a las demás unidades (día de inicio de la
/// Visita en [día del inicio del tramo, día del fin del tramo)). Sin él, el conteo es el del Excel completo.
/// </para>
/// <para>
/// <b>Mes cerrado</b>: a diferencia de las demás unidades, que en un mes cerrado y cubierto entero salen de la foto del
/// cierre, este conteo no tiene foto: se lee siempre en vivo de las asignaciones, cuyo criterio ya está congelado
/// (<c>VisitaTrabajador.EsExtranjeroCobrable</c>). Una Visita cancelada o cambiada de día después del cierre sí altera la
/// cifra de un mes cerrado, y una asignación hecha tarde a una Visita del mes también cuenta; es la misma cifra que el
/// Excel, a propósito, para que número y exportación no diverjan.
/// </para>
/// </summary>
public record ContarExtranjerosCobrablesDelMesQuery(
    int Anyo, int Mes, IReadOnlyDictionary<Guid, IReadOnlyList<TramoDeVigencia>>? TramosPorTenant = null)
    : IRequest<ConteoExtranjerosCobrablesDelMesDto>;

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
            .Where(f => request.TramosPorTenant is not { } tramos
                        || (tramos.TryGetValue(f.TenantId, out var delTenant) && EnAlgunTramo(f.FechaIngreso, delTenant)))
            .GroupBy(f => new { f.TenantId, f.TenantNombre })
            .Select(g => new ConteoExtranjerosCobrablesTenantDto(
                g.Key.TenantId, g.Key.TenantNombre, g.Count(), g.Select(f => f.TrabajadorId).Distinct().Count()))
            .OrderBy(t => t.TenantNombre, StringComparer.CurrentCulture)
            .ToList();

        return new ConteoExtranjerosCobrablesDelMesDto(
            extranjeros.Anyo, extranjeros.Mes, porTenant, extranjeros.TenantsSinAlcanceCompleto, extranjeros.SinCifras);
    }

    /// <summary>
    /// Si el día de inicio de la Visita cae dentro de algún tramo: desde el día de negocio del inicio del tramo y antes del
    /// día de negocio de su fin (la Visita del día en que empieza la Asignación cuenta; la del día en que se cierra, no).
    /// </summary>
    public static bool EnAlgunTramo(DateOnly diaDeLaVisita, IEnumerable<TramoDeVigencia> tramos) =>
        tramos.Any(t => diaDeLaVisita >= DiaDeNegocio.De(t.DesdeUtc) && diaDeLaVisita < DiaDeNegocio.De(t.HastaUtc));
}