using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerOpcionesFiltroCentros;

/// <summary>Qué columna de la lista de Centros alimenta las opciones del filtro.</summary>
public enum CampoFiltroCentros
{
    /// <summary>La Empresa en posición Cliente empresarial del Centro.</summary>
    ClienteEmpresarial,

    /// <summary>La Empresa que trabaja en el Centro.</summary>
    Empresa,
}

/// <summary>
/// Opciones de los filtros «Cliente empresarial» y «Empresa» de /centros: las Empresas que aparecen en
/// algún Centro visible. Usa el mismo alcance de visibilidad que <c>ObtenerCentrosQuery</c>
/// (<see cref="IAlcanceDatosService.ObtenerCentroIdsVisiblesAsync"/>), para que el filtro ofrezca justo
/// lo que la lista puede llegar a enseñar: los selectores del alta usan alcances de gestión, que para
/// un usuario de solo lectura o del portal quedan vacíos aunque vea Centros.
/// </summary>
public record ObtenerOpcionesFiltroCentrosQuery(CampoFiltroCentros Campo)
    : IRequest<IReadOnlyList<OpcionFiltroCentrosDto>>;

public record OpcionFiltroCentrosDto(Guid Id, string RazonSocial);

public class ObtenerOpcionesFiltroCentrosQueryHandler(
    ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerOpcionesFiltroCentrosQuery, IReadOnlyList<OpcionFiltroCentrosDto>>
{
    public async Task<IReadOnlyList<OpcionFiltroCentrosDto>> Handle(
        ObtenerOpcionesFiltroCentrosQuery request, CancellationToken cancellationToken)
    {
        var centros = centrosContext.Centros.AsQueryable();

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            centros = centros.Where(c => centroIdsVisibles.Contains(c.Id));

        var empresaIds = request.Campo == CampoFiltroCentros.ClienteEmpresarial
            ? centros.Select(c => c.ClienteId)
            : centros.Select(c => c.EmpresaId);

        return await empresasContext.Empresas
            .Where(e => empresaIds.Contains(e.Id))
            .OrderBy(e => e.RazonSocial)
            .Select(e => new OpcionFiltroCentrosDto(e.Id, e.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
