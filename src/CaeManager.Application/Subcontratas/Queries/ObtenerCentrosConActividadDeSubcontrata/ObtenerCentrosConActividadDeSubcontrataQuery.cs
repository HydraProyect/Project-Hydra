using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;

/// <summary>
/// Equivalente de <see cref="Empresas.Queries.ObtenerCentrosConActividadDeEmpresa.ObtenerCentrosConActividadDeEmpresaQuery"/>
/// para Subcontrata — misma derivación (Subcontrata → Trabajadores →
/// Asignaciones activas → Centro), sin relación directa en el modelo.
/// </summary>
public record ObtenerCentrosConActividadDeSubcontrataQuery(Guid SubcontrataId)
    : IRequest<IReadOnlyList<CentroConActividadDto>>;

public record CentroConActividadDto(Guid Id, string Nombre, string ClienteRazonSocial, int TrabajadoresAsignados);

public class ObtenerCentrosConActividadDeSubcontrataQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCentrosConActividadDeSubcontrataQuery, IReadOnlyList<CentroConActividadDto>>
{
    public async Task<IReadOnlyList<CentroConActividadDto>> Handle(
        ObtenerCentrosConActividadDeSubcontrataQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.SubcontrataVisibleAsync(request.SubcontrataId, cancellationToken))
            return [];

        var filas = await (
            from asignacion in dbContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in dbContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.SubcontrataId == request.SubcontrataId
            join centro in dbContext.Centros on asignacion.CentroId equals centro.Id
            join cliente in dbContext.Clientes on centro.ClienteId equals cliente.Id
            select new { centro.Id, centro.Nombre, ClienteRazonSocial = cliente.RazonSocial, TrabajadorId = trabajador.Id })
            .ToListAsync(cancellationToken);

        return filas
            .GroupBy(x => new { x.Id, x.Nombre, x.ClienteRazonSocial })
            .Select(g => new CentroConActividadDto(
                g.Key.Id, g.Key.Nombre, g.Key.ClienteRazonSocial, g.Select(x => x.TrabajadorId).Distinct().Count()))
            .OrderBy(d => d.Nombre)
            .ToList();
    }
}
