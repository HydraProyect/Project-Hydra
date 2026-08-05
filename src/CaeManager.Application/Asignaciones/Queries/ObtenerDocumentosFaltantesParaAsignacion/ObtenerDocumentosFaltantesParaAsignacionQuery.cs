using CaeManager.Application.Alertas;
using CaeManager.Application.Centros;
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
    ITrabajadoresQueryContext trabajadoresContext, ICentrosQueryContext centrosContext, IDocumentosFaltantesService servicio)
    : IRequestHandler<ObtenerDocumentosFaltantesParaAsignacionQuery, IReadOnlyList<DocumentoFaltanteDto>>
{
    public async Task<IReadOnlyList<DocumentoFaltanteDto>> Handle(
        ObtenerDocumentosFaltantesParaAsignacionQuery request, CancellationToken cancellationToken)
    {
        if (request.TrabajadorIds.Count == 0 || request.CentroIds.Count == 0)
            return [];

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => request.TrabajadorIds.Contains(t.Id))
            .Select(t => new { t.Id, Nombre = t.Nombre + " " + t.Apellidos })
            .ToListAsync(cancellationToken);

        var centros = await centrosContext.Centros
            .Where(c => request.CentroIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre })
            .ToListAsync(cancellationToken);

        var parejas = trabajadores
            .SelectMany(t => centros.Select(c => new ParejaTrabajadorCentro(t.Id, t.Nombre, c.Id, c.Nombre)))
            .ToList();

        return await servicio.CalcularAsync(parejas, cancellationToken);
    }
}
