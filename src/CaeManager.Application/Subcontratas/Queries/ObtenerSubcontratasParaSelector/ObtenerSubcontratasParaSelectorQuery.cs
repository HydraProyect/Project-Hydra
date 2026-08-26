using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;

/// <summary>
/// Lista ligera para poblar selectores. Con EmpresaId, se restringe a las
/// Subcontratas que ya prestan servicio a esa Empresa — sin EmpresaId,
/// devuelve todas.
///
/// F3b-Subcontrata (revisión adversaria del 2026-08-26, misma lección que
/// <c>ObtenerClientesParaSelectorQuery</c>): a diferencia de la rama
/// Subcontrata de <c>BuscarGlobalQuery</c> (congelada, sin flujo de una sola
/// sesión que dependa de ella), este selector se adelanta a leer Empresas —
/// el drawer "Nuevo trabajador" (radio "Subcontrata") lo usa para poblar el
/// desplegable, y una Subcontrata creada en la misma sesión necesita
/// aparecer ahí de inmediato. Ver
/// f3b-subcontrata-selector-adelantado-2026-08-26.md.
/// </summary>
public record ObtenerSubcontratasParaSelectorQuery(Guid? EmpresaId = null) : IRequest<IReadOnlyList<SubcontrataSelectorDto>>;

public record SubcontrataSelectorDto(Guid Id, string RazonSocial);

public class ObtenerSubcontratasParaSelectorQueryHandler(IEmpresasQueryContext empresasContext, ISubcontratasQueryContext dbContext)
    : IRequestHandler<ObtenerSubcontratasParaSelectorQuery, IReadOnlyList<SubcontrataSelectorDto>>
{
    public async Task<IReadOnlyList<SubcontrataSelectorDto>> Handle(
        ObtenerSubcontratasParaSelectorQuery request, CancellationToken cancellationToken)
    {
        var consulta = empresasContext.Empresas.Where(e => e.NivelServicio != null);

        if (request.EmpresaId is not null)
        {
            var subcontrataIdsAsociadas = dbContext.SubcontratasEmpresas
                .Where(se => se.EmpresaId == request.EmpresaId)
                .Select(se => se.SubcontrataId);

            consulta = consulta.Where(s => subcontrataIdsAsociadas.Contains(s.Id));
        }

        return await consulta
            .OrderBy(s => s.RazonSocial)
            .Select(s => new SubcontrataSelectorDto(s.Id, s.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
