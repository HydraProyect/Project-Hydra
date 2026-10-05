using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerResumenClientesDeEmpresas;

/// <summary>
/// Columna «Presta servicio a» de /empresas (maqueta aprobada de la fase 1): para las Empresas de una
/// página, a cuántos Clientes empresariales presta servicio cada una y cuál es el primero por razón social.
/// Una sola consulta por página, no una por fila.
/// </summary>
/// <param name="EmpresaIds">Las Empresas de la página.</param>
public record ObtenerResumenClientesDeEmpresasQuery(IReadOnlyList<Guid> EmpresaIds)
    : IRequest<IReadOnlyDictionary<Guid, ResumenClientesDeEmpresaDto>>;

/// <param name="Total">Clientes empresariales a los que presta servicio (Relación Empresarial vigente).</param>
/// <param name="Primero">El primero por razón social: el mismo que encabeza el desplegable de la fila.</param>
public record ResumenClientesDeEmpresaDto(int Total, string Primero);

/// <summary>
/// Mismo criterio que <see cref="ObtenerClientesDeEmpresaQuery"/>, aplicado a varias Empresas: el mismo
/// predicado (<see cref="ClientesVigentesDeEmpresas"/>) y el mismo alcance de GESTIÓN, no de lectura
/// (REC-153: la cartera comercial de una contratista no es documentación del Cliente, y un usuario de portal
/// —rol Cliente— no debe verla). Una Empresa fuera de ese alcance no aparece en el resultado, igual que una
/// sin Clientes empresariales: la pantalla no puede distinguir los dos casos, como el desplegable (#810).
/// </summary>
public class ObtenerResumenClientesDeEmpresasQueryHandler(IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerResumenClientesDeEmpresasQuery, IReadOnlyDictionary<Guid, ResumenClientesDeEmpresaDto>>
{
    public async Task<IReadOnlyDictionary<Guid, ResumenClientesDeEmpresaDto>> Handle(
        ObtenerResumenClientesDeEmpresasQuery request, CancellationToken cancellationToken)
    {
        var pedidas = request.EmpresaIds.Distinct().ToList();
        if (pedidas.Count == 0)
            return new Dictionary<Guid, ResumenClientesDeEmpresaDto>();

        // null = sin restricción; lista (incluida vacía, la del rol Cliente) = solo esas.
        var gestionables = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
        var permitidas = gestionables is null ? pedidas : pedidas.Where(gestionables.Contains).ToList();
        if (permitidas.Count == 0)
            return new Dictionary<Guid, ResumenClientesDeEmpresaDto>();

        var filas = await ClientesVigentesDeEmpresas.De(empresasContext, permitidas)
            .OrderBy(f => f.RazonSocial)
            .Select(f => new { f.EmpresaId, f.RazonSocial })
            .ToListAsync(cancellationToken);

        // GroupBy conserva el orden de la consulta: el primero de cada grupo es el primero por razón social.
        return filas
            .GroupBy(f => f.EmpresaId)
            .ToDictionary(g => g.Key, g => new ResumenClientesDeEmpresaDto(g.Count(), g.First().RazonSocial));
    }
}
