using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas.AltaDeVisita;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerPlanAltaVisita;

/// <summary>
/// Vista previa de «Crear visita» (decisión de Chris, 2026-11-19): qué Trabajadores quedarán asignados al Centro y qué
/// Gestiones nacerán, con la misma regla que aplicará <c>CrearVisitaCommand</c> con <c>AsignarYCrearGestiones</c>
/// (<see cref="IPlanAltaVisitaService"/>). Crear la Visita es la única confirmación: esto no escribe nada.
///
/// <para>
/// Mismas comprobaciones que el Command, en el mismo orden: el Centro dentro del alcance de gestión
/// (<c>CentroParaGestionVisibleAsync</c>) y los Trabajadores admisibles (<see cref="TrabajadoresAdmisiblesEnVisita"/>, que no
/// se relaja). Si algo no pasa, <c>null</c>, igual que «no existe», para no revelar qué hay fuera.
/// <see cref="PlanAltaVisitaPreviaDto.PuedeAsignar"/> dice si quien crea tiene autoridad para dar de alta las Asignaciones
/// Trabajador–Centro que hacen falta (<see cref="IAutoridadAsignacionesService"/>): sin ella, el Command rechazará la Visita.
/// </para>
/// </summary>
[AreaDelEncargo("Visitas")]
public record ObtenerPlanAltaVisitaQuery(Guid CentroId, DateOnly FechaFin, IReadOnlyList<Guid> TrabajadorIds)
    : IRequest<PlanAltaVisitaPreviaDto?>;

public record PlanAltaVisitaPreviaDto(PlanAltaVisitaDto Plan, bool PuedeAsignar);

public class ObtenerPlanAltaVisitaQueryHandler(
    ICentrosQueryContext centrosContext, ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos, IAutoridadAsignacionesService autoridad, IPlanAltaVisitaService plan)
    : IRequestHandler<ObtenerPlanAltaVisitaQuery, PlanAltaVisitaPreviaDto?>
{
    public async Task<PlanAltaVisitaPreviaDto?> Handle(ObtenerPlanAltaVisitaQuery request, CancellationToken cancellationToken)
    {
        var empresaId = await centrosContext.Centros
            .Where(c => c.Id == request.CentroId)
            .Select(c => (Guid?)c.EmpresaId)
            .FirstOrDefaultAsync(cancellationToken);
        if (empresaId is null || !await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, cancellationToken))
            return null;

        var trabajadorIds = (request.TrabajadorIds ?? []).Distinct().ToList();

        // Mismo tope que el Command con AsignarYCrearGestiones (CrearVisitaCommandValidator): por encima, el Command rechazará
        // la Visita, así que no hay plan que enseñar.
        if (trabajadorIds.Count > CrearAsignacionesCommandValidator.MaximoTrabajadoresPorLote)
            return null;

        if (trabajadorIds.Count == 0)
            return new PlanAltaVisitaPreviaDto(PlanAltaVisitaDto.Vacio, PuedeAsignar: true);

        var admisibles = await TrabajadoresAdmisiblesEnVisita.CargarAsync(
            trabajadorIds, empresaId.Value, trabajadoresContext, empresasContext, alcanceDatos, cancellationToken);
        if (admisibles is null)
            return null;

        // El mismo «hoy» con el que el Command daría de alta las Asignaciones y mediría el solape DEC-19.
        var resultado = await plan.CalcularAsync(
            request.CentroId, request.FechaFin, DiaDeNegocio.Hoy(), trabajadorIds, cancellationToken);
        var puedeAsignar = await AutoridadParaAsignar.TieneAsync(
            autoridad, request.CentroId, resultado.Asignaciones.Select(a => a.TrabajadorId).ToList(), cancellationToken);

        return new PlanAltaVisitaPreviaDto(resultado, puedeAsignar);
    }
}
