using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;

/// <summary>
/// Fase C (Bandeja del gestor): un Trabajador con Asignación activa a un
/// Centro donde falta un Documento Vigente de un TipoDocumento marcado
/// <c>TipoDocumentoCentro.BloqueaAcceso</c> (PLAN-EJECUCION-UX.md § 0.4) —
/// sustituye a ObtenerRequisitosDocumentalesPendientesQuery/RequisitoDocumental
/// (retirados): antes era un check manual a nivel de Centro
/// (BloqueaAcceso+Cumplido sin trabajador asociado), ahora es automático y
/// por trabajador, igual criterio que <c>CalculoEstadoCentroService</c>
/// (misma fuente: solo los huecos que además fuerzan <c>EstadoCentro.Bloqueado</c>,
/// porque son los únicos con urgencia real para una cola priorizada).
/// </summary>
public record ObtenerDocumentacionBloqueantePendienteQuery : IRequest<IReadOnlyList<DocumentacionBloqueantePendienteDto>>;

public record DocumentacionBloqueantePendienteDto(
    Guid CentroId, string CentroNombre, Guid TrabajadorId, string TrabajadorNombre,
    Guid TipoDocumentoId, string TipoDocumentoNombre);

public class ObtenerDocumentacionBloqueantePendienteQueryHandler(
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    IDocumentosQueryContext documentosContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentacionBloqueantePendienteQuery, IReadOnlyList<DocumentacionBloqueantePendienteDto>>
{
    public async Task<IReadOnlyList<DocumentacionBloqueantePendienteDto>> Handle(
        ObtenerDocumentacionBloqueantePendienteQuery request, CancellationToken cancellationToken)
    {
        var filasBloqueantes = await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tc.Incluido && tc.BloqueaAcceso)
            .Select(tc => new { tc.TipoDocumentoId, tc.CentroId })
            .ToListAsync(cancellationToken);

        if (filasBloqueantes.Count == 0)
            return [];

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            filasBloqueantes = filasBloqueantes.Where(f => centroIdsVisibles.Contains(f.CentroId)).ToList();

        if (filasBloqueantes.Count == 0)
            return [];

        var centroIds = filasBloqueantes.Select(f => f.CentroId).Distinct().ToList();
        var tipoIds = filasBloqueantes.Select(f => f.TipoDocumentoId).Distinct().ToList();

        var centros = await centrosContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre })
            .ToDictionaryAsync(c => c.Id, c => c.Nombre, cancellationToken);

        var tipos = await tiposDocumentoContext.TiposDocumento
            .Where(t => tipoIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var asignaciones = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null && centroIds.Contains(asignacion.CentroId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            select new
            {
                asignacion.CentroId,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos
            })
            .ToListAsync(cancellationToken);

        if (asignaciones.Count == 0)
            return [];

        var trabajadorIds = asignaciones.Select(a => a.TrabajadorId).Distinct().ToList();

        var parejasConDocumentoVigente = (await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIds.Contains(d.TipoDocumentoId)
                && (d.FechaVencimiento == null || d.FechaVencimiento >= DateOnly.FromDateTime(DateTime.UtcNow)))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId })
            .ToListAsync(cancellationToken))
            .Select(d => (d.TrabajadorId, d.TipoDocumentoId))
            .ToHashSet();

        var pendientes = new List<DocumentacionBloqueantePendienteDto>();

        foreach (var fila in filasBloqueantes)
        {
            if (!centros.TryGetValue(fila.CentroId, out var centroNombre)) continue;
            if (!tipos.TryGetValue(fila.TipoDocumentoId, out var tipoNombre)) continue;

            foreach (var asignacion in asignaciones.Where(a => a.CentroId == fila.CentroId))
            {
                if (parejasConDocumentoVigente.Contains((asignacion.TrabajadorId, fila.TipoDocumentoId)))
                    continue;

                pendientes.Add(new DocumentacionBloqueantePendienteDto(
                    fila.CentroId, centroNombre, asignacion.TrabajadorId, asignacion.TrabajadorNombre,
                    fila.TipoDocumentoId, tipoNombre));
            }
        }

        return pendientes.OrderBy(p => p.CentroNombre).ThenBy(p => p.TrabajadorNombre).ToList();
    }
}
