using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Proyectos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Queries.RequisitosEvento;

/// <summary>Requisitos documentales de un evento; vacío si el Proyecto no existe o el actor no lo ve.</summary>
public record ObtenerRequisitosEventoQuery(Guid ProyectoId) : IRequest<IReadOnlyList<RequisitoEventoDto>>;

public record RequisitoEventoDto(Guid TipoDocumentoId, string Nombre, AmbitoAplicacion AmbitoAplicacion);

/// <summary>
/// De dónde puede copiar sus requisitos un evento nuevo del Cliente empresarial dado: sus plantillas y sus
/// eventos con requisitos. Solo de ese Cliente: la autoridad es la del Proyecto (<see cref="ProyectoAutorizacion"/>).
/// </summary>
public record ObtenerOrigenesRequisitosEventoQuery(Guid ClienteId) : IRequest<OrigenesRequisitosEventoDto>;

public record OrigenRequisitosDto(Guid Id, string Nombre, int Requisitos);

public record OrigenesRequisitosEventoDto(IReadOnlyList<OrigenRequisitosDto> Plantillas, IReadOnlyList<OrigenRequisitosDto> Eventos)
{
    public static readonly OrigenesRequisitosEventoDto Vacio = new([], []);
}

public class ObtenerRequisitosEventoQueryHandler(
    IProyectosQueryContext proyectosContext, IRequisitosEventoQueryContext requisitosContext, ITiposDocumentoQueryContext tiposContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerRequisitosEventoQuery, IReadOnlyList<RequisitoEventoDto>>
{
    public async Task<IReadOnlyList<RequisitoEventoDto>> Handle(ObtenerRequisitosEventoQuery request, CancellationToken cancellationToken)
    {
        var proyecto = await proyectosContext.Proyectos
            .Where(p => p.Id == request.ProyectoId).Select(p => new { p.ClienteId }).FirstOrDefaultAsync(cancellationToken);
        if (proyecto is null || !await ProyectoAutorizacion.VisibleAsync(proyecto.ClienteId, alcanceDatos, cancellationToken))
            return [];

        return await (
            from requisito in requisitosContext.ProyectosRequisitos
            where requisito.ProyectoId == request.ProyectoId
            join tipo in tiposContext.TiposDocumento on requisito.TipoDocumentoId equals tipo.Id
            orderby tipo.AmbitoAplicacion, tipo.Nombre
            select new RequisitoEventoDto(tipo.Id, tipo.Nombre, tipo.AmbitoAplicacion))
            .ToListAsync(cancellationToken);
    }
}

public class ObtenerOrigenesRequisitosEventoQueryHandler(
    IProyectosQueryContext proyectosContext, IRequisitosEventoQueryContext requisitosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerOrigenesRequisitosEventoQuery, OrigenesRequisitosEventoDto>
{
    public async Task<OrigenesRequisitosEventoDto> Handle(ObtenerOrigenesRequisitosEventoQuery request, CancellationToken cancellationToken)
    {
        if (request.ClienteId == Guid.Empty || !await ProyectoAutorizacion.VisibleAsync(request.ClienteId, alcanceDatos, cancellationToken))
            return OrigenesRequisitosEventoDto.Vacio;

        var plantillas = await requisitosContext.PlantillasRequisitosEvento
            .Where(p => p.ClienteId == request.ClienteId)
            .OrderBy(p => p.Nombre)
            .Select(p => new OrigenRequisitosDto(p.Id, p.Nombre,
                requisitosContext.PlantillasRequisitosEventoTipos.Count(t => t.PlantillaId == p.Id)))
            .ToListAsync(cancellationToken);

        var eventos = await proyectosContext.Proyectos
            .Where(p => p.ClienteId == request.ClienteId && p.Tipo == TipoProyecto.Evento)
            .Select(p => new { p.Id, p.Nombre, p.FechaInicio, Requisitos = requisitosContext.ProyectosRequisitos.Count(r => r.ProyectoId == p.Id) })
            .Where(p => p.Requisitos > 0)
            .OrderByDescending(p => p.FechaInicio).ThenBy(p => p.Nombre)
            .Select(p => new OrigenRequisitosDto(p.Id, p.Nombre, p.Requisitos))
            .ToListAsync(cancellationToken);

        return new OrigenesRequisitosEventoDto(plantillas, eventos);
    }
}
