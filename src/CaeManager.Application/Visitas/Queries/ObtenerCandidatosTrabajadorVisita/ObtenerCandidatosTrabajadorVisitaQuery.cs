using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerCandidatosTrabajadorVisita;

/// <summary>
/// Trabajadores que se pueden añadir a una Visita desde el panel: los del selector de «Editar
/// visita» (<see cref="AlcanceSelectorTrabajadores.Cartera"/>, pedido a la misma consulta para que
/// las dos listas no puedan divergir) que además son de la Empresa de la Visita
/// (<see cref="TrabajadoresAdmisiblesEnVisita"/>, S-B-1), menos los que ya entran. Vacía si la
/// Visita no existe, está cancelada o su Centro queda fuera del alcance de GESTIÓN de quien
/// pregunta: quien no puede añadir tampoco ve a quién podría añadir.
/// </summary>
public record ObtenerCandidatosTrabajadorVisitaQuery(Guid VisitaId) : IRequest<IReadOnlyList<TrabajadorSelectorDto>>;

public class ObtenerCandidatosTrabajadorVisitaQueryHandler(
    IVisitasQueryContext visitasContext, ICentrosQueryContext centrosContext, ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos, ISender sender)
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

        var empresaId = await centrosContext.Centros
            .Where(c => c.Id == visita.CentroId)
            .Select(c => (Guid?)c.EmpresaId)
            .FirstOrDefaultAsync(cancellationToken);
        if (empresaId is not { } empresa)
            return [];

        var yaEntran = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == request.VisitaId)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        var ofrecidos = await sender.Send(
            new ObtenerTrabajadoresParaSelectorQuery(AlcanceSelectorTrabajadores.Cartera), cancellationToken);

        var admisibles = (await TrabajadoresAdmisiblesEnVisita.FiltrarAsync(
                ofrecidos.Select(t => t.Id).Except(yaEntran).ToList(), empresa,
                trabajadoresContext, empresasContext, alcanceDatos, cancellationToken))
            .ToHashSet();

        return ofrecidos.Where(t => admisibles.Contains(t.Id)).ToList();
    }
}
