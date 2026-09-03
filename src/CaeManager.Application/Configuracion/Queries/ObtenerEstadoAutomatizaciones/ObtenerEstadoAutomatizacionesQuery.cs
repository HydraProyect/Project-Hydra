using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Configuracion.Queries.ObtenerEstadoAutomatizaciones;

/// <summary>
/// Alimenta el panel "Automatizaciones" de Configuración (Parte XVI PROMPT
/// 08). Une el catálogo fijo de trabajos (<see cref="CatalogoAutomatizaciones"/>)
/// con su estado real por tenant — un trabajo sin fila todavía (nunca se
/// ejecutó ni se tocó su interruptor) se muestra Activo por defecto, igual
/// que hace la propia entidad.
/// </summary>
public record ObtenerEstadoAutomatizacionesQuery : IRequest<IReadOnlyList<AutomatizacionDto>>;

public record AutomatizacionDto(
    string Id, string Nombre, string Descripcion, bool Conmutable, bool Activo,
    DateTime? UltimaEjecucionUtc, bool? UltimoResultadoExitoso,
    string? UltimoMensajeError = null, int? UltimosElementosEvaluados = null, int? UltimosElementosAfectados = null,
    DateTime? ProximoCicloUtc = null);

public class ObtenerEstadoAutomatizacionesQueryHandler(IConfiguracionQueryContext configuracionContext)
    : IRequestHandler<ObtenerEstadoAutomatizacionesQuery, IReadOnlyList<AutomatizacionDto>>
{
    public async Task<IReadOnlyList<AutomatizacionDto>> Handle(
        ObtenerEstadoAutomatizacionesQuery request, CancellationToken cancellationToken)
    {
        var estados = await configuracionContext.EstadosAutomatizacion
            .ToDictionaryAsync(e => e.TrabajoId, cancellationToken);

        return CatalogoAutomatizaciones.Trabajos
            .Select(trabajo =>
            {
                var estado = estados.GetValueOrDefault(trabajo.Id);

                // Sin Cadencia (sondeo en segundos) o sin ejecución previa
                // todavía, no hay nada que calcular: el panel lo trata como
                // "Continuo" o "—" respectivamente.
                var proximoCiclo = trabajo.Cadencia is { } cadencia && estado?.UltimaEjecucionUtc is { } ultima
                    ? ultima + cadencia
                    : (DateTime?)null;

                return new AutomatizacionDto(
                    trabajo.Id, trabajo.Nombre, trabajo.Descripcion, trabajo.Conmutable,
                    estado?.Activo ?? true, estado?.UltimaEjecucionUtc, estado?.UltimoResultadoExitoso,
                    estado?.UltimoMensajeError, estado?.UltimosElementosEvaluados, estado?.UltimosElementosAfectados,
                    proximoCiclo);
            })
            .ToList();
    }
}
