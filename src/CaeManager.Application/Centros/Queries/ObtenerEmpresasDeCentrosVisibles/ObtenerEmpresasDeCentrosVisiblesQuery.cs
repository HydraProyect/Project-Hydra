using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerEmpresasDeCentrosVisibles;

/// <summary>
/// Opciones del filtro «Empresa» de /centros: las Empresas que trabajan en algún Centro que
/// quien mira ve. Mismo alcance que la lista (<see cref="IAlcanceDatosService.ObtenerCentroIdsVisiblesAsync"/>,
/// el de <c>ObtenerCentrosQuery</c>), así que cada opción devuelve al menos un Centro y ninguna
/// nombra una Empresa que la lista no muestre ya en sus filas.
///
/// No sirve <c>ObtenerEmpresasParaSelectorQuery</c>: acota por el alcance de <b>gestión</b>
/// (<see cref="IAlcanceDatosService.ObtenerEmpresaIdsParaGestionAsync"/>), que para un usuario de
/// portal (rol Cliente) va vacío aunque vea Centros con su Empresa; el filtro solo le ofrecía «Todas».
/// Solo lectura para filtrar: no alimenta ningún comando.
/// </summary>
public record ObtenerEmpresasDeCentrosVisiblesQuery : IRequest<IReadOnlyList<EmpresaDeCentroDto>>;

public record EmpresaDeCentroDto(Guid Id, string RazonSocial);

public class ObtenerEmpresasDeCentrosVisiblesQueryHandler(
    ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEmpresasDeCentrosVisiblesQuery, IReadOnlyList<EmpresaDeCentroDto>>
{
    public async Task<IReadOnlyList<EmpresaDeCentroDto>> Handle(
        ObtenerEmpresasDeCentrosVisiblesQuery request, CancellationToken cancellationToken)
    {
        var centros = centrosContext.Centros.AsQueryable();

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            centros = centros.Where(c => centroIdsVisibles.Contains(c.Id));

        var empresaIds = centros.Select(c => c.EmpresaId).Distinct();

        return await empresasContext.Empresas
            .Where(e => empresaIds.Contains(e.Id))
            .OrderBy(e => e.RazonSocial)
            .ThenBy(e => e.Id)
            .Select(e => new EmpresaDeCentroDto(e.Id, e.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
