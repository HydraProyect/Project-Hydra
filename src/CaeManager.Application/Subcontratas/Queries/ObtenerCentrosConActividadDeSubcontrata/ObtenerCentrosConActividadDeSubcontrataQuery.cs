using CaeManager.Application.Centros;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
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
public record ObtenerCentrosConActividadDeSubcontrataQuery(Guid SubcontrataId)
    : IRequest<IReadOnlyList<CentroConActividadDto>>;

public class ObtenerCentrosConActividadDeSubcontrataQueryHandler(IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCentrosConActividadDeSubcontrataQuery, IReadOnlyList<CentroConActividadDto>>
{
    public async Task<IReadOnlyList<CentroConActividadDto>> Handle(
        ObtenerCentrosConActividadDeSubcontrataQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.SubcontrataVisibleAsync(request.SubcontrataId, cancellationToken))
            return [];

        var centrosVisibles = centrosContext.Centros;
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            centrosVisibles = centrosVisibles.Where(c => centroIdsVisibles.Contains(c.Id));

        var filas = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.SubcontrataId == request.SubcontrataId
            join centro in centrosVisibles on asignacion.CentroId equals centro.Id
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            select new FilaActividadCentro(centro.Id, centro.Nombre, cliente.RazonSocial, trabajador.Id))
            .ToListAsync(cancellationToken);

        return CentroConActividadAgrupador.Agrupar(filas);
    }
}
