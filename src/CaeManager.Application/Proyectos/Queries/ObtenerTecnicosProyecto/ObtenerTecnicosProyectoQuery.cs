using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;

public record ObtenerTecnicosProyectoQuery(Guid ProyectoId) : IRequest<IReadOnlyList<TecnicoProyectoDto>>;

public record TecnicoProyectoDto(
    Guid Id, Guid TrabajadorId, string TrabajadorNombreCompleto, string? TrabajadorDni,
    DateOnly FechaAlta, DateOnly? FechaBaja, bool EstaActivo);

public class ObtenerTecnicosProyectoQueryHandler(IProyectosQueryContext proyectosContext, ITrabajadoresQueryContext trabajadoresContext)
    : IRequestHandler<ObtenerTecnicosProyectoQuery, IReadOnlyList<TecnicoProyectoDto>>
{
    public async Task<IReadOnlyList<TecnicoProyectoDto>> Handle(ObtenerTecnicosProyectoQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from proyectoTecnico in proyectosContext.ProyectosTecnicos
            where proyectoTecnico.ProyectoId == request.ProyectoId
            join trabajador in trabajadoresContext.Trabajadores on proyectoTecnico.TrabajadorId equals trabajador.Id
            orderby proyectoTecnico.FechaAlta descending
            select new TecnicoProyectoDto(
                proyectoTecnico.Id, trabajador.Id, trabajador.Nombre + " " + trabajador.Apellidos, trabajador.Dni,
                proyectoTecnico.FechaAlta, proyectoTecnico.FechaBaja, proyectoTecnico.FechaBaja == null);

        return await consulta.ToListAsync(cancellationToken);
    }
}
