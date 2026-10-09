using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;

public record ObtenerTecnicosProyectoQuery(Guid ProyectoId) : IRequest<IReadOnlyList<TecnicoProyectoDto>>;

public record TecnicoProyectoDto(
    Guid Id, Guid TrabajadorId, string TrabajadorNombreCompleto,
    DateOnly FechaAlta, DateOnly? FechaBaja, bool EstaActivo);

/// <summary>
/// Los técnicos de un Proyecto solo se nombran a quien ve el Proyecto: mismo criterio que
/// <c>ObtenerProyectoPorIdQuery</c> y que los comandos (<see cref="ProyectoAutorizacion"/>). Sin
/// esta guarda la autorización de la lectura dependía de que el componente pidiera antes el
/// detalle.
///
/// <para>
/// No se cruza con <c>ObtenerTrabajadorIdsVisiblesAsync</c> porque hoy sería redundante: quien ve
/// un Proyecto tiene alcance total dentro del Tenant propietario o una Asignación de Cartera, que
/// es siempre sobre el Tenant entero (D-7) y alcanza a todos sus Trabajadores. Solo dejaría de
/// serlo bajo una Asignación de Operación acotada a un Cliente empresarial, que ningún código de
/// producción crea (<c>RepartoDeCarteraPorClienteRetiradoTests</c>); la invariante y ese caso
/// están medidos bajo RLS en <c>TecnicosDeProyectoDentroDelAlcanceBajoRuntimeTests</c>.
/// </para>
/// </summary>
public class ObtenerTecnicosProyectoQueryHandler(IProyectosQueryContext proyectosContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerTecnicosProyectoQuery, IReadOnlyList<TecnicoProyectoDto>>
{
    public async Task<IReadOnlyList<TecnicoProyectoDto>> Handle(ObtenerTecnicosProyectoQuery request, CancellationToken cancellationToken)
    {
        var clienteIdsDelProyecto = await proyectosContext.Proyectos
            .Where(p => p.Id == request.ProyectoId)
            .Select(p => p.ClienteId)
            .ToListAsync(cancellationToken);

        // Inexistente o fuera de alcance: la misma lista vacía, sin distinguir un caso del otro.
        if (clienteIdsDelProyecto.Count == 0
            || !await ProyectoAutorizacion.VisibleAsync(clienteIdsDelProyecto[0], alcanceDatos, cancellationToken))
            return [];

        var consulta =
            from proyectoTecnico in proyectosContext.ProyectosTecnicos
            where proyectoTecnico.ProyectoId == request.ProyectoId
            join trabajador in trabajadoresContext.Trabajadores on proyectoTecnico.TrabajadorId equals trabajador.Id
            orderby proyectoTecnico.FechaAlta descending
            select new TecnicoProyectoDto(
                proyectoTecnico.Id, trabajador.Id, trabajador.Nombre + " " + trabajador.Apellidos,
                proyectoTecnico.FechaAlta, proyectoTecnico.FechaBaja, proyectoTecnico.FechaBaja == null);

        return await consulta.ToListAsync(cancellationToken);
    }
}
