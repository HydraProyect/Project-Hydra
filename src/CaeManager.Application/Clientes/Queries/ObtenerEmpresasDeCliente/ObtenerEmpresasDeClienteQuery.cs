using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;

/// <summary>Respalda la pestaña "Empresas" del Context Workspace de Cliente.</summary>
public record ObtenerEmpresasDeClienteQuery(Guid ClienteId) : IRequest<IReadOnlyList<EmpresaDeClienteDto>>;

public record EmpresaDeClienteDto(Guid Id, string RazonSocial, string? Cif);

/// <summary>
/// F4.2b: repuntado de <c>EmpresaCliente</c> a <c>RelacionEmpresarial</c>.
/// Espejo exacto de <c>ObtenerSubcontratasDeClienteQuery</c>, y por el mismo
/// motivo: un Cliente servido a la vez por una Empresa propia y por una
/// Subcontrata es la situación corriente, así que filtrar la proveedora por
/// <c>EsPropia</c> es requisito de corrección, no defensa en profundidad —
/// sin él, la Subcontrata aparecería en la pestaña "Empresas". Mismo
/// discriminador que ya usa <c>AlcanceDatosService.ObtenerEmpresaIdsVisiblesAsync</c>.
/// </summary>
public class ObtenerEmpresasDeClienteQueryHandler(IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEmpresasDeClienteQuery, IReadOnlyList<EmpresaDeClienteDto>>
{
    public async Task<IReadOnlyList<EmpresaDeClienteDto>> Handle(
        ObtenerEmpresasDeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken))
            return [];

        return await (
            from r in dbContext.RelacionesEmpresariales
            where r.ClienteId == request.ClienteId && r.VigenciaHasta == null
            join empresa in dbContext.Empresas.Where(e => e.EsPropia)
                on r.ProveedoraId equals empresa.Id
            orderby empresa.RazonSocial
            select new EmpresaDeClienteDto(empresa.Id, empresa.RazonSocial, empresa.Cif))
            .ToListAsync(cancellationToken);
    }
}
