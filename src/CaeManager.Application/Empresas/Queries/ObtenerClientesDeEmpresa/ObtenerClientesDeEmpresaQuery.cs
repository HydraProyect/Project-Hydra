using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;

/// <summary>
/// Respalda la pestaña "Clientes" del Context Workspace de Empresa — mismo
/// criterio que <c>ObtenerEmpresasDeClienteQuery</c>, en la dirección
/// contraria.
/// </summary>
public record ObtenerClientesDeEmpresaQuery(Guid EmpresaId) : IRequest<IReadOnlyList<ClienteDeEmpresaDto>>;

public record ClienteDeEmpresaDto(Guid Id, string RazonSocial, string? Cif);

/// <summary>
/// F4.2b: repuntado de <c>EmpresaCliente</c> a <c>RelacionEmpresarial</c>.
/// Aquí el lado fijado es la proveedora y el ambiguo la contraparte:
/// <c>RelacionEmpresarial.ClienteId</c> contiene un Cliente real en las
/// shapes Empresa→Cliente y Subcontrata→Cliente, pero una <b>Empresa
/// propia</b> en la shape Subcontrata→Empresa. Como
/// <c>EmpresaVisibleAsync</c> no comprueba tipo bajo acceso total, sin
/// filtrar por <c>EsCritico != null</c> una Empresa propia podría acabar
/// listada como "Cliente de la Empresa".
/// </summary>
public class ObtenerClientesDeEmpresaQueryHandler(IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerClientesDeEmpresaQuery, IReadOnlyList<ClienteDeEmpresaDto>>
{
    public async Task<IReadOnlyList<ClienteDeEmpresaDto>> Handle(
        ObtenerClientesDeEmpresaQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaVisibleAsync(request.EmpresaId, cancellationToken))
            return [];

        return await (
            from r in empresasContext.RelacionesEmpresariales
            where r.ProveedoraId == request.EmpresaId && r.VigenciaHasta == null
            join cliente in empresasContext.Empresas.Where(e => e.EsCritico != null)
                on r.ClienteId equals cliente.Id
            orderby cliente.RazonSocial
            select new ClienteDeEmpresaDto(cliente.Id, cliente.RazonSocial, cliente.Cif))
            .ToListAsync(cancellationToken);
    }
}
