using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;

/// <summary>Respalda la pestaña "Subcontratas" del Context Workspace de Cliente, vía la tabla puente SubcontrataCliente.</summary>
public record ObtenerSubcontratasDeClienteQuery(Guid ClienteId) : IRequest<IReadOnlyList<SubcontrataDeClienteDto>>;

public record SubcontrataDeClienteDto(Guid Id, string RazonSocial);

public class ObtenerSubcontratasDeClienteQueryHandler(ISubcontratasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSubcontratasDeClienteQuery, IReadOnlyList<SubcontrataDeClienteDto>>
{
    public async Task<IReadOnlyList<SubcontrataDeClienteDto>> Handle(
        ObtenerSubcontratasDeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken))
            return [];

        return await (
            from sc in dbContext.SubcontratasClientes
            where sc.ClienteId == request.ClienteId
            join subcontrata in dbContext.Subcontratas on sc.SubcontrataId equals subcontrata.Id
            orderby subcontrata.RazonSocial
            select new SubcontrataDeClienteDto(subcontrata.Id, subcontrata.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
