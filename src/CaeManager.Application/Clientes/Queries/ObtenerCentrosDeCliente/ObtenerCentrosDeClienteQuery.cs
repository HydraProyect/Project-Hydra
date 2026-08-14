using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerCentrosDeCliente;

/// <summary>
/// Respalda la pestaña "Centros" del Context Workspace de Cliente — relación
/// directa (<c>Centro.ClienteId</c> es FK real, a diferencia de la actividad
/// derivada que usa el lado de Empresa, ver <c>ObtenerClientesDeEmpresaQuery</c>
/// y su comentario en <c>EmpresaWorkspacePanel.razor</c>): un Cliente es dueño
/// de todos sus Centros, los opere la Empresa que los opere.
/// </summary>
public record ObtenerCentrosDeClienteQuery(Guid ClienteId) : IRequest<IReadOnlyList<CentroDeClienteDto>>;

public record CentroDeClienteDto(Guid Id, string Nombre, string EmpresaRazonSocial);

public class ObtenerCentrosDeClienteQueryHandler(ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCentrosDeClienteQuery, IReadOnlyList<CentroDeClienteDto>>
{
    public async Task<IReadOnlyList<CentroDeClienteDto>> Handle(
        ObtenerCentrosDeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken))
            return [];

        return await (
            from centro in centrosContext.Centros
            where centro.ClienteId == request.ClienteId
            join empresa in empresasContext.Empresas on centro.EmpresaId equals empresa.Id
            orderby centro.Nombre
            select new CentroDeClienteDto(centro.Id, centro.Nombre, empresa.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
