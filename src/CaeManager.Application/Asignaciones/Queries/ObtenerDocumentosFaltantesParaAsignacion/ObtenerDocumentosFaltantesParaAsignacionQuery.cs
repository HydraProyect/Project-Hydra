using CaeManager.Application.Alertas;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Asignaciones.Queries.ObtenerDocumentosFaltantesParaAsignacion;

/// <summary>
/// Preflight no bloqueante (Fase B5, UX_PATTERNS.md § "Asignar trabajador a
/// centro/cliente") — antes de confirmar una asignación en lote, qué
/// documentos obligatorios le faltarían a cada Trabajador en cada Centro del
/// producto cartesiano elegido. Reutiliza <see cref="IDocumentosFaltantesService"/>,
/// la misma regla que ya usa <c>/alertas</c>, aplicada aquí a parejas
/// todavía no existentes en vez de a Asignaciones reales.
/// </summary>
public record ObtenerDocumentosFaltantesParaAsignacionQuery(
    IReadOnlyList<Guid> TrabajadorIds, IReadOnlyList<Guid> CentroIds) : IRequest<IReadOnlyList<DocumentoFaltanteDto>>;

public class ObtenerDocumentosFaltantesParaAsignacionQueryHandler(
    ITrabajadoresQueryContext trabajadoresContext, ICentrosQueryContext centrosContext,
    IDocumentosFaltantesService servicio, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentosFaltantesParaAsignacionQuery, IReadOnlyList<DocumentoFaltanteDto>>
{
    public async Task<IReadOnlyList<DocumentoFaltanteDto>> Handle(
        ObtenerDocumentosFaltantesParaAsignacionQuery request, CancellationToken cancellationToken)
    {
        if (request.TrabajadorIds.Count == 0 || request.CentroIds.Count == 0)
            return [];

        // Alcance de LECTURA, no autoridad de escritura — esa la impone
        // CrearAsignacionesCommand vía IAutoridadAsignacionesService cuando se
        // confirme el alta. A este preflight le basta con que quien consulta
        // pueda VER el Centro, mismo criterio que ObtenerCentrosParaSelectorQuery
        // (el propio selector que alimenta a los tres llamadores de esta
        // consulta). Un CentroId fuera de cartera se descarta en silencio,
        // igual que las consultas *PorId* (ver AlcanceDatosServiceExtensions):
        // no se distingue «no existe» de «no es tuyo», así que esto nunca es un
        // error explícito, solo un resultado más corto.
        //
        // El Trabajador NO se acota aquí a propósito. IAlcanceDatosService ya
        // documenta por qué el selector de Trabajador es universal dentro del
        // tenant (un mismo Trabajador presta servicio a varios Clientes de
        // distintos Gestores CAE) — acotarlo aquí con
        // ObtenerTrabajadorIdsVisiblesAsync (que exige una Asignación ACTIVA a
        // un Centro visible) rompería el propio caso de uso de este preflight:
        // un Trabajador nuevo o todavía sin ninguna Asignación (alta desde el
        // drawer N×M, o "Asignar desde visita" en AcordeonAsignacionesCentro)
        // es justo el que se está a punto de asignar por primera vez, y
        // quedaría descartado en silencio antes de poder avisar de sus
        // documentos faltantes.
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var centroIdsEnAlcance = centroIdsVisibles is null
            ? request.CentroIds
            : request.CentroIds.Where(centroIdsVisibles.ToHashSet().Contains).ToList();

        if (centroIdsEnAlcance.Count == 0) return [];

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => request.TrabajadorIds.Contains(t.Id))
            .Select(t => new { t.Id, Nombre = t.Nombre + " " + t.Apellidos })
            .ToListAsync(cancellationToken);

        var centros = await centrosContext.Centros
            .Where(c => centroIdsEnAlcance.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre })
            .ToListAsync(cancellationToken);

        var parejas = trabajadores
            .SelectMany(t => centros.Select(c => new ParejaTrabajadorCentro(t.Id, t.Nombre, c.Id, c.Nombre)))
            .ToList();

        return await servicio.CalcularAsync(parejas, cancellationToken);
    }
}
