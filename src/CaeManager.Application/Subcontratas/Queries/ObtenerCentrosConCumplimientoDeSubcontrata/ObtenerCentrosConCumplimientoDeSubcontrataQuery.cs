using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConCumplimientoDeSubcontrata;

public record ObtenerCentrosConCumplimientoDeSubcontrataQuery(Guid Id) : IRequest<IReadOnlyList<CentroCumplimientoSubcontrataDto>>;
public record CentroCumplimientoSubcontrataDto(CentroConActividadDto Centro, FraccionCumplimiento Cumplimiento, EstadoDocumento? PeorEstado);

/// <summary>
/// Fracción por Centro de los documentos de sus Trabajadores exigidos allí: suma los pares Trabajador–Tipo
/// del lector contextual existente, sin truncar por las 50 filas de la página ni deduplicar tipos entre Centros.
/// No es el porcentaje global de la Empresa ni el de todos los Trabajadores del Centro. No escribe.
/// </summary>
public class ObtenerCentrosConCumplimientoDeSubcontrataQueryHandler(IMediator mediator, IAlcanceDatosService alcance)
    : IRequestHandler<ObtenerCentrosConCumplimientoDeSubcontrataQuery, IReadOnlyList<CentroCumplimientoSubcontrataDto>>
{
    public async Task<IReadOnlyList<CentroCumplimientoSubcontrataDto>> Handle(ObtenerCentrosConCumplimientoDeSubcontrataQuery request, CancellationToken cancellationToken)
    {
        if (!await alcance.SubcontrataVisibleAsync(request.Id, cancellationToken)) return [];
        var centros = await mediator.Send(new ObtenerCentrosConActividadDeSubcontrataQuery(request.Id), cancellationToken);
        var resultado = new List<CentroCumplimientoSubcontrataDto>();
        // Lecturas secuenciales: los handlers comparten el DbContext del mismo Tenant propietario.
        foreach (var centro in centros)
        {
            if (!await alcance.CentroVisibleAsync(centro.Id, cancellationToken)) continue;
            var trabajadores = await mediator.Send(new ObtenerAsignacionesDocumentacionPorCentroQuery(centro.Id,
                EmpresaEmpleadoraSubcontratistaId: request.Id), cancellationToken);
            var fraccion = new FraccionCumplimiento(trabajadores.Sum(t => t.Cumplimiento.AlDia), trabajadores.Sum(t => t.Cumplimiento.Requeridos));
            var estados = trabajadores.Where(t => t.Cumplimiento.Requeridos > 0).Select(t => t.PeorEstado).ToList();
            resultado.Add(new(centro, fraccion, estados.Count == 0 ? null : estados.MinBy(SeveridadEstadoDocumento.Rango)));
        }
        return resultado;
    }
}
