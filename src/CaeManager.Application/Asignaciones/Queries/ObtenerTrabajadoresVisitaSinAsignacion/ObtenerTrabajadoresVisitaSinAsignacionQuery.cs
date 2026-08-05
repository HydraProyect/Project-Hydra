using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Asignaciones.Queries.ObtenerTrabajadoresVisitaSinAsignacion;

/// <summary>
/// "Asignación rápida desde visita" (PLAN-EJECUCION-UX.md § 0.3): trabajadores
/// que la próxima visita del centro indica que asistirán (<c>VisitaTrabajador</c>,
/// ya vinculados a un <c>Trabajador</c> real) pero sin Asignación activa en
/// ese Centro todavía — el aviso que dispara la acción "Asignar" desde el
/// acordeón.
/// </summary>
public record ObtenerTrabajadoresVisitaSinAsignacionQuery(Guid VisitaId, Guid CentroId)
    : IRequest<IReadOnlyList<TrabajadorSinAsignacionDto>>;

public record TrabajadorSinAsignacionDto(Guid TrabajadorId, string TrabajadorNombre);

public class ObtenerTrabajadoresVisitaSinAsignacionQueryHandler(
    IVisitasQueryContext visitasContext, ITrabajadoresQueryContext trabajadoresContext, IAsignacionesQueryContext asignacionesContext)
    : IRequestHandler<ObtenerTrabajadoresVisitaSinAsignacionQuery, IReadOnlyList<TrabajadorSinAsignacionDto>>
{
    public async Task<IReadOnlyList<TrabajadorSinAsignacionDto>> Handle(
        ObtenerTrabajadoresVisitaSinAsignacionQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsDeVisita = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == request.VisitaId)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        if (trabajadorIdsDeVisita.Count == 0)
            return [];

        var trabajadorIdsYaAsignados = await asignacionesContext.Asignaciones
            .Where(a => a.CentroId == request.CentroId && a.FechaBaja == null && trabajadorIdsDeVisita.Contains(a.TrabajadorId))
            .Select(a => a.TrabajadorId)
            .ToListAsync(cancellationToken);

        var faltantes = trabajadorIdsDeVisita.Except(trabajadorIdsYaAsignados).ToList();
        if (faltantes.Count == 0)
            return [];

        return await trabajadoresContext.Trabajadores
            .Where(t => faltantes.Contains(t.Id))
            .OrderBy(t => t.Apellidos).ThenBy(t => t.Nombre)
            .Select(t => new TrabajadorSinAsignacionDto(t.Id, t.Nombre + " " + t.Apellidos))
            .ToListAsync(cancellationToken);
    }
}
