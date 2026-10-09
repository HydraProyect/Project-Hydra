using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerCandidatosTrabajadorVisita;

/// <summary>
/// Trabajadores que se pueden añadir a una Visita desde el panel: los mismos que ofrece el
/// selector de «Editar visita» (<see cref="AlcanceSelectorTrabajadores.BaseGeneralDelTenant"/>,
/// pedido a la misma consulta para que las dos listas no puedan divergir) menos los que ya
/// entran. Vacía si la Visita no existe, está cancelada o su Centro queda fuera del alcance de
/// GESTIÓN de quien pregunta: quien no puede añadir tampoco ve a quién podría añadir.
/// </summary>
public record ObtenerCandidatosTrabajadorVisitaQuery(Guid VisitaId) : IRequest<IReadOnlyList<TrabajadorSelectorDto>>;

public class ObtenerCandidatosTrabajadorVisitaQueryHandler(
    IVisitasQueryContext visitasContext, IAlcanceDatosService alcanceDatos, ISender sender)
    : IRequestHandler<ObtenerCandidatosTrabajadorVisitaQuery, IReadOnlyList<TrabajadorSelectorDto>>
{
    public async Task<IReadOnlyList<TrabajadorSelectorDto>> Handle(
        ObtenerCandidatosTrabajadorVisitaQuery request, CancellationToken cancellationToken)
    {
        var visita = await visitasContext.Visitas
            .Where(v => v.Id == request.VisitaId)
            .Select(v => new { v.CentroId, v.EstaCancelada })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null || visita.EstaCancelada
            || !await alcanceDatos.CentroParaGestionVisibleAsync(visita.CentroId, cancellationToken))
            return [];

        var yaEntran = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == request.VisitaId)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        var ofrecidos = await sender.Send(
            new ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores.BaseGeneralDelTenant), cancellationToken);

        return ofrecidos.Where(t => !yaEntran.Contains(t.Id)).ToList();
    }
}
