using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Application.Clientes.Queries.ObtenerCentrosDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConCumplimientoDeSubcontrata;
using MediatR;

namespace CaeManager.Application.Clientes.Queries.ObtenerSubcontratasConCentrosDeCliente;

[AreaDelEncargo("Clientes")]
public record ObtenerSubcontratasConCentrosDeClienteQuery(Guid Id) : IRequest<IReadOnlyList<SubcontrataConCentrosDeClienteDto>>;
public record SubcontrataConCentrosDeClienteDto(SubcontrataDeClienteDto Subcontrata, IReadOnlyList<CentroCumplimientoSubcontrataDto> Centros);

/// <summary>
/// Documentación de los Trabajadores de cada Empresa contraparte en los Centros visibles del Cliente empresarial.
/// Cruza pertenencia completa (sin paginación) con visibilidad, nunca por razón social ni con el porcentaje global.
/// Los lectores se ejecutan secuencialmente porque comparten el DbContext del Tenant propietario. No escribe.
/// <para>
/// Lecturas por lote, no por Subcontrata ni por Centro: una para los Centros donde trabaja cada Subcontrata visible
/// (<see cref="ObtenerCentrosConActividadDeSubcontratasQuery"/>, la misma derivación que la de una Subcontrata) y una
/// para el cumplimiento de todos los pares Subcontrata y Centro (<see cref="ObtenerCumplimientoDeAsignacionesPorCentrosYEmpresasQuery"/>,
/// el mismo cálculo y alcance que la de un Centro). El número de lecturas no crece con las Subcontratas ni con los Centros.
/// </para>
/// </summary>
public class ObtenerSubcontratasConCentrosDeClienteQueryHandler(IMediator mediator, IAlcanceDatosService alcance)
    : IRequestHandler<ObtenerSubcontratasConCentrosDeClienteQuery, IReadOnlyList<SubcontrataConCentrosDeClienteDto>>
{
    public async Task<IReadOnlyList<SubcontrataConCentrosDeClienteDto>> Handle(ObtenerSubcontratasConCentrosDeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcance.ClienteVisibleAsync(request.Id, cancellationToken)) return [];
        var propios = await mediator.Send(new ObtenerCentrosDeClienteQuery(request.Id), cancellationToken);
        // CentroVisibleAsync y SubcontrataVisibleAsync son estas mismas comprobaciones, Centro a Centro y Subcontrata a Subcontrata.
        var centroIdsVisibles = await alcance.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var visibles = propios
            .Select(c => c.Id)
            .Where(id => centroIdsVisibles is null || centroIdsVisibles.Contains(id))
            .ToHashSet();
        var subcontratas = await mediator.Send(new ObtenerSubcontratasDeClienteQuery(request.Id), cancellationToken);
        var subcontrataIdsVisibles = await alcance.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        var subcontratasVisibles = subcontratas
            .Where(s => subcontrataIdsVisibles is null || subcontrataIdsVisibles.Contains(s.Id))
            .ToList();

        var actividad = visibles.Count == 0 || subcontratasVisibles.Count == 0
            ? new Dictionary<Guid, IReadOnlyList<CentroConActividadDto>>()
            : await mediator.Send(new ObtenerCentrosConActividadDeSubcontratasQuery(
                subcontratasVisibles.Select(s => s.Id).Distinct().ToList()), cancellationToken);
        // Intersectar antes de leer documentos: los Centros de otros Clientes empresariales no se calculan.
        var centrosDeCliente = new Dictionary<Guid, List<CentroConActividadDto>>();
        foreach (var subcontrata in subcontratasVisibles)
            centrosDeCliente[subcontrata.Id] = (actividad.GetValueOrDefault(subcontrata.Id) ?? [])
                .Where(c => visibles.Contains(c.Id))
                .ToList();

        var conCentros = centrosDeCliente.Where(par => par.Value.Count > 0).ToList();
        var cumplimiento = conCentros.Count == 0
            ? new Dictionary<Guid, IReadOnlyDictionary<Guid, IReadOnlyList<CumplimientoDeAsignacionDto>>>()
            : await mediator.Send(new ObtenerCumplimientoDeAsignacionesPorCentrosYEmpresasQuery(
                conCentros.SelectMany(par => par.Value).Select(c => c.Id).Distinct().ToList(),
                conCentros.Select(par => par.Key).ToList()), cancellationToken);

        var resultado = new List<SubcontrataConCentrosDeClienteDto>();
        foreach (var subcontrata in subcontratasVisibles)
        {
            var porCentro = cumplimiento.GetValueOrDefault(subcontrata.Id);
            var centros = new List<CentroCumplimientoSubcontrataDto>();
            foreach (var centro in centrosDeCliente[subcontrata.Id])
            {
                var trabajadores = porCentro?.GetValueOrDefault(centro.Id) ?? [];
                var fraccion = new FraccionCumplimiento(trabajadores.Sum(t => t.Cumplimiento.AlDia), trabajadores.Sum(t => t.Cumplimiento.Requeridos));
                var estados = trabajadores.Where(t => t.Cumplimiento.Requeridos > 0).Select(t => t.PeorEstado).ToList();
                centros.Add(new(centro, fraccion, estados.Count == 0 ? null : estados.MinBy(SeveridadEstadoDocumento.Rango)));
            }
            resultado.Add(new(subcontrata, centros));
        }
        return resultado;
    }
}
