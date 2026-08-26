using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;

/// <summary>
/// Lista completa y ligera para poblar selectores (p. ej. al crear un
/// Centro). A diferencia de los selectores de Empresa/Subcontrata/
/// Trabajador/Vehículo (que muestran el catálogo global a propósito, para
/// poder reutilizar un registro ya existente sin duplicarlo), este sí se
/// acota al alcance del usuario — un Cliente es la raíz de la cartera, no
/// tiene sentido "reutilizar" el Cliente de otro Gestor CAE.
///
/// F3b (revisión adversaria del 2026-08-26): a diferencia de
/// ObtenerClientesQuery/BuscarClientePorCifQuery (congeladas por D2 hasta
/// F4), este selector se adelanta a leer Empresas — un Cliente creado en la
/// misma sesión que un Centro/Empresa que lo referencia (el asistente de
/// alta guiada) necesita verlo de inmediato; la evidencia real de un E2E en
/// rojo demostró que congelarlo también rompía ese flujo, no solo dejaba
/// una lista vieja. Ver f3b-selectores-adelantados-2026-08-26.md.
/// </summary>
public record ObtenerClientesParaSelectorQuery : IRequest<IReadOnlyList<ClienteSelectorDto>>;

public record ClienteSelectorDto(Guid Id, string RazonSocial);

public class ObtenerClientesParaSelectorQueryHandler(IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerClientesParaSelectorQuery, IReadOnlyList<ClienteSelectorDto>>
{
    public async Task<IReadOnlyList<ClienteSelectorDto>> Handle(
        ObtenerClientesParaSelectorQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Empresas.Where(e => e.EsCritico != null).AsQueryable();

        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null)
            consulta = consulta.Where(c => clienteIdsVisibles.Contains(c.Id));

        return await consulta
            .OrderBy(c => c.RazonSocial)
            .Select(c => new ClienteSelectorDto(c.Id, c.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
