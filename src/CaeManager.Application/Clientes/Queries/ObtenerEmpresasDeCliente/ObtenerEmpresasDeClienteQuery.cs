using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;

/// <summary>Respalda la pestaña "Empresas" del Context Workspace de Cliente, vía la tabla puente EmpresaCliente.</summary>
public record ObtenerEmpresasDeClienteQuery(Guid ClienteId) : IRequest<IReadOnlyList<EmpresaDeClienteDto>>;

public record EmpresaDeClienteDto(Guid Id, string RazonSocial, string? Cif);

public class ObtenerEmpresasDeClienteQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEmpresasDeClienteQuery, IReadOnlyList<EmpresaDeClienteDto>>
{
    public async Task<IReadOnlyList<EmpresaDeClienteDto>> Handle(
        ObtenerEmpresasDeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken))
            return [];

        return await (
            from ec in dbContext.EmpresasClientes
            where ec.ClienteId == request.ClienteId
            join empresa in dbContext.Empresas on ec.EmpresaId equals empresa.Id
            orderby empresa.RazonSocial
            select new EmpresaDeClienteDto(empresa.Id, empresa.RazonSocial, empresa.Cif))
            .ToListAsync(cancellationToken);
    }
}
