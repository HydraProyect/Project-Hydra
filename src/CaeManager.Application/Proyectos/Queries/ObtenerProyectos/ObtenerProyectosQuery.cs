using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Trabajadores;
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
    bool EstaAbierto)
{
    /// <summary>
    /// Técnicos con alta vigente en el proyecto, por nombre. Es lo que enseña el recuento de la
    /// fila del listado; el historial completo (bajas incluidas) lo da ObtenerTecnicosProyectoQuery.
    /// </summary>
    public IReadOnlyList<TecnicoActivoListaDto> TecnicosActivos { get; init; } = [];
}

/// <summary>Técnico activo de un proyecto en la lista: la persona, escrita «Nombre Apellidos».</summary>
public record TecnicoActivoListaDto(Guid TrabajadorId, string NombreCompleto);

public class ObtenerProyectosQueryHandler(ICentrosQueryContext centrosContext, IProyectosQueryContext proyectosContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerProyectosQuery, IReadOnlyList<ProyectoListaDto>>
{
    public async Task<IReadOnlyList<ProyectoListaDto>> Handle(ObtenerProyectosQuery request, CancellationToken cancellationToken)
    {
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null && !clienteIdsVisibles.Contains(request.ClienteId))
            return [];

        var consulta =
            from proyecto in proyectosContext.Proyectos
            where proyecto.ClienteId == request.ClienteId
            join centro in centrosContext.Centros on proyecto.CentroId equals centro.Id
            select new { proyecto, centro.Nombre };

        var proyectos = await consulta
            .OrderByDescending(x => x.proyecto.FechaInicio)
            .Select(x => new ProyectoListaDto(
                x.proyecto.Id, x.proyecto.Nombre, x.proyecto.CentroId, x.Nombre,
                x.proyecto.FechaInicio, x.proyecto.FechaFinPrevista, x.proyecto.FechaCierreReal, x.proyecto.FechaCierreReal == null))
            .ToListAsync(cancellationToken);

        if (request.SoloAbiertos is bool soloAbiertos)
            proyectos = proyectos.Where(p => p.EstaAbierto == soloAbiertos).ToList();

        if (proyectos.Count == 0)
            return proyectos;

        // Una sola consulta para los técnicos de toda la lista (no una por fila). El join con
        // Trabajadores aplica sus filtros de consulta, igual que ObtenerTecnicosProyectoQuery:
        // un técnico cuyo Trabajador no es visible no se nombra.
        var proyectoIds = proyectos.Select(p => p.Id).ToList();
        var tecnicos = await (
            from proyectoTecnico in proyectosContext.ProyectosTecnicos
            where proyectoIds.Contains(proyectoTecnico.ProyectoId) && proyectoTecnico.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on proyectoTecnico.TrabajadorId equals trabajador.Id
            orderby trabajador.Nombre, trabajador.Apellidos
            select new { proyectoTecnico.ProyectoId, trabajador.Id, Nombre = trabajador.Nombre + " " + trabajador.Apellidos })
            .ToListAsync(cancellationToken);

        var tecnicosPorProyecto = tecnicos.ToLookup(t => t.ProyectoId, t => new TecnicoActivoListaDto(t.Id, t.Nombre));
        return proyectos
            .Select(p => p with { TecnicosActivos = tecnicosPorProyecto[p.Id].ToList() })
            .ToList();
    }
}
