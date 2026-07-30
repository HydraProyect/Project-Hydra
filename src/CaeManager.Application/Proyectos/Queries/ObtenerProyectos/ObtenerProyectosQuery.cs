using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Queries.ObtenerProyectos;

public record ObtenerProyectosQuery(Guid ClienteId, bool? SoloAbiertos = null) : IRequest<IReadOnlyList<ProyectoListaDto>>;

public record ProyectoListaDto(
    Guid Id,
    string Nombre,
    Guid CentroId,
    string CentroNombre,
    DateOnly FechaInicio,
    DateOnly? FechaFinPrevista,
    DateOnly? FechaCierreReal,
    bool EstaAbierto);

public class ObtenerProyectosQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerProyectosQuery, IReadOnlyList<ProyectoListaDto>>
{
    public async Task<IReadOnlyList<ProyectoListaDto>> Handle(ObtenerProyectosQuery request, CancellationToken cancellationToken)
    {
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null && !clienteIdsVisibles.Contains(request.ClienteId))
            return [];

        var consulta =
            from proyecto in dbContext.Proyectos
            where proyecto.ClienteId == request.ClienteId
            join centro in dbContext.Centros on proyecto.CentroId equals centro.Id
            select new { proyecto, centro.Nombre };

        var proyectos = await consulta
            .OrderByDescending(x => x.proyecto.FechaInicio)
            .Select(x => new ProyectoListaDto(
                x.proyecto.Id, x.proyecto.Nombre, x.proyecto.CentroId, x.Nombre,
                x.proyecto.FechaInicio, x.proyecto.FechaFinPrevista, x.proyecto.FechaCierreReal, x.proyecto.FechaCierreReal == null))
            .ToListAsync(cancellationToken);

        return request.SoloAbiertos is bool soloAbiertos
            ? proyectos.Where(p => p.EstaAbierto == soloAbiertos).ToList()
            : proyectos;
    }
}
