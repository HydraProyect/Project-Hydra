using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;

/// <summary>
/// Respalda la pestaña "Clientes" del Context Workspace de Empresa, vía la
/// tabla puente EmpresaCliente — mismo criterio que
/// <c>ObtenerEmpresasDeClienteQuery</c>, en la dirección contraria.
/// </summary>
public record ObtenerClientesDeEmpresaQuery(Guid EmpresaId) : IRequest<IReadOnlyList<ClienteDeEmpresaDto>>;

public record ClienteDeEmpresaDto(Guid Id, string RazonSocial, string? Cif);

public class ObtenerClientesDeEmpresaQueryHandler(IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerClientesDeEmpresaQuery, IReadOnlyList<ClienteDeEmpresaDto>>
{
    public async Task<IReadOnlyList<ClienteDeEmpresaDto>> Handle(
        ObtenerClientesDeEmpresaQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaVisibleAsync(request.EmpresaId, cancellationToken))
            return [];

        return await (
            from ec in empresasContext.EmpresasClientes
            where ec.EmpresaId == request.EmpresaId
            // EmpresaCliente.ClienteId ya apunta a Empresas (F3).
            join cliente in empresasContext.Empresas on ec.ClienteId equals cliente.Id
            orderby cliente.RazonSocial
            select new ClienteDeEmpresaDto(cliente.Id, cliente.RazonSocial, cliente.Cif))
            .ToListAsync(cancellationToken);
    }
}
