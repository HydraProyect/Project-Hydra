using CaeManager.Application.Clientes.Queries.ObtenerCentrosDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConCumplimientoDeSubcontrata;
using MediatR;

namespace CaeManager.Application.Clientes.Queries.ObtenerSubcontratasConCentrosDeCliente;

public record ObtenerSubcontratasConCentrosDeClienteQuery(Guid Id) : IRequest<IReadOnlyList<SubcontrataConCentrosDeClienteDto>>;
public record SubcontrataConCentrosDeClienteDto(SubcontrataDeClienteDto Subcontrata, IReadOnlyList<CentroCumplimientoSubcontrataDto> Centros);

/// <summary>
/// Documentación de los Trabajadores de cada Empresa contraparte en los Centros visibles del Cliente empresarial.
/// Cruza pertenencia completa (sin paginación) con visibilidad, nunca por razón social ni con el porcentaje global.
/// Los lectores se ejecutan secuencialmente porque comparten el DbContext del Tenant propietario. No escribe.
/// </summary>
public class ObtenerSubcontratasConCentrosDeClienteQueryHandler(IMediator mediator, IAlcanceDatosService alcance)
    : IRequestHandler<ObtenerSubcontratasConCentrosDeClienteQuery, IReadOnlyList<SubcontrataConCentrosDeClienteDto>>
{
    public async Task<IReadOnlyList<SubcontrataConCentrosDeClienteDto>> Handle(ObtenerSubcontratasConCentrosDeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcance.ClienteVisibleAsync(request.Id, cancellationToken)) return [];
        var propios = await mediator.Send(new ObtenerCentrosDeClienteQuery(request.Id), cancellationToken);
        var visibles = new HashSet<Guid>();
        foreach (var centro in propios)
            if (await alcance.CentroVisibleAsync(centro.Id, cancellationToken)) visibles.Add(centro.Id);
        var subcontratas = await mediator.Send(new ObtenerSubcontratasDeClienteQuery(request.Id), cancellationToken);
        var resultado = new List<SubcontrataConCentrosDeClienteDto>();
        foreach (var subcontrata in subcontratas)
        {
            if (!await alcance.SubcontrataVisibleAsync(subcontrata.Id, cancellationToken)) continue;
            var centros = visibles.Count == 0 ? [] : await mediator.Send(new ObtenerCentrosConCumplimientoDeSubcontrataQuery(subcontrata.Id), cancellationToken);
            resultado.Add(new(subcontrata, centros.Where(c => visibles.Contains(c.Centro.Id)).ToList()));
        }
        return resultado;
    }
}
