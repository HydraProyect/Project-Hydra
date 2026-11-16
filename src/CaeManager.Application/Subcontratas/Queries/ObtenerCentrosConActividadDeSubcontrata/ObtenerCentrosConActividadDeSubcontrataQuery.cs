using CaeManager.Application.Centros;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;

/// <summary>
/// Equivalente de <see cref="Empresas.Queries.ObtenerCentrosConActividadDeEmpresa.ObtenerCentrosConActividadDeEmpresaQuery"/>
/// para Subcontrata — misma derivación (Subcontrata → Trabajadores →
/// Asignaciones activas → Centro), sin relación directa en el modelo.
///
/// Que la Subcontrata sea visible no hace visibles todos los Centros donde
/// trabaja: solo se devuelven los Centros visibles para quien pregunta
/// (<see cref="IAlcanceDatosService.ObtenerCentroIdsVisiblesAsync"/>, el
/// alcance de <c>ObtenerCentrosQuery</c>). Sin ese cruce, el usuario de un
/// Cliente empresarial leía aquí el nombre de los Centros de otros Clientes
/// empresariales del mismo Tenant propietario y su razón social.
/// </summary>
[AreaDelEncargo("Subcontratas")]
public record ObtenerCentrosConActividadDeSubcontrataQuery(Guid SubcontrataId)
    : IRequest<IReadOnlyList<CentroConActividadDto>>;

/// <summary>
/// La misma derivación y el mismo alcance que <see cref="ObtenerCentrosConActividadDeSubcontrataQuery"/> para varias
/// Subcontratas en una sola consulta: cada Subcontrata no visible se omite (no trae clave) y una visible sin actividad en
/// ningún Centro visible tampoco. Cada lista es la que devolvería la consulta de esa Subcontrata sola.
/// </summary>
[AreaDelEncargo("Subcontratas")]
public record ObtenerCentrosConActividadDeSubcontratasQuery(IReadOnlyCollection<Guid> SubcontrataIds)
    : IRequest<IReadOnlyDictionary<Guid, IReadOnlyList<CentroConActividadDto>>>;

public class ObtenerCentrosConActividadDeSubcontrataQueryHandler(IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCentrosConActividadDeSubcontrataQuery, IReadOnlyList<CentroConActividadDto>>
{
    private readonly ObtenerCentrosConActividadDeSubcontratasQueryHandler lote = new(
        asignacionesContext, centrosContext, empresasContext, trabajadoresContext, alcanceDatos);

    public async Task<IReadOnlyList<CentroConActividadDto>> Handle(
        ObtenerCentrosConActividadDeSubcontrataQuery request, CancellationToken cancellationToken)
    {
        var porSubcontrata = await lote.Handle(new([request.SubcontrataId]), cancellationToken);
        return porSubcontrata.GetValueOrDefault(request.SubcontrataId) ?? [];
    }
}

public class ObtenerCentrosConActividadDeSubcontratasQueryHandler(IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCentrosConActividadDeSubcontratasQuery, IReadOnlyDictionary<Guid, IReadOnlyList<CentroConActividadDto>>>
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CentroConActividadDto>>> Handle(
        ObtenerCentrosConActividadDeSubcontratasQuery request, CancellationToken cancellationToken)
    {
        // SubcontrataVisibleAsync es esta misma comprobación sobre ObtenerSubcontrataIdsVisiblesAsync.
        var subcontrataIdsVisibles = await alcanceDatos.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        var subcontrataIds = request.SubcontrataIds
            .Where(id => subcontrataIdsVisibles is null || subcontrataIdsVisibles.Contains(id))
            .Distinct()
            .ToList();
        if (subcontrataIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<CentroConActividadDto>>();

        var centrosVisibles = centrosContext.Centros;
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            centrosVisibles = centrosVisibles.Where(c => centroIdsVisibles.Contains(c.Id));

        var filas = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.SubcontrataId != null && subcontrataIds.Contains(trabajador.SubcontrataId.Value)
            join centro in centrosVisibles on asignacion.CentroId equals centro.Id
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            select new
            {
                SubcontrataId = trabajador.SubcontrataId!.Value,
                Fila = new FilaActividadCentro(centro.Id, centro.Nombre, cliente.RazonSocial, trabajador.Id)
            })
            .ToListAsync(cancellationToken);

        return filas
            .GroupBy(f => f.SubcontrataId)
            .ToDictionary(g => g.Key, g => CentroConActividadAgrupador.Agrupar(g.Select(f => f.Fila)));
    }
}
