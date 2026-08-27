using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;

/// <summary>Respalda la pestaña "Subcontratas" del Context Workspace de Cliente.</summary>
public record ObtenerSubcontratasDeClienteQuery(Guid ClienteId) : IRequest<IReadOnlyList<SubcontrataDeClienteDto>>;

public record SubcontrataDeClienteDto(Guid Id, string RazonSocial);

/// <summary>
/// F4.2b (2026-08-27): repuntado de <c>SubcontratasClientes</c> a
/// <c>RelacionesEmpresariales</c>. El JOIN a <c>Empresas.Where(NivelServicio
/// != null)</c> no es defensa en profundidad: un Cliente con una Empresa
/// propia Y una Subcontrata sirviéndole a la vez es la situación normal, no
/// un caso límite — sin el filtro, la Empresa propia se colaría en "las
/// Subcontratas del Cliente" en cualquier tenant con actividad real.
/// Verificado por revisión adversarial independiente antes de implementar.
/// </summary>
public class ObtenerSubcontratasDeClienteQueryHandler(
    IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSubcontratasDeClienteQuery, IReadOnlyList<SubcontrataDeClienteDto>>
{
    public async Task<IReadOnlyList<SubcontrataDeClienteDto>> Handle(
        ObtenerSubcontratasDeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken))
            return [];

        return await (
            from r in empresasContext.RelacionesEmpresariales
            where r.ClienteId == request.ClienteId && r.VigenciaHasta == null
            join subcontrata in empresasContext.Empresas.Where(e => e.NivelServicio != null)
                on r.ProveedoraId equals subcontrata.Id
            orderby subcontrata.RazonSocial
            select new SubcontrataDeClienteDto(subcontrata.Id, subcontrata.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
