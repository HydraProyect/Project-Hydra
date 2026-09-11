using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa;

/// <summary>Detecciones pendientes (sin resolver) de altas/bajas de personal para una Empresa — ver DeteccionTrabajadoresService.</summary>
public record ObtenerDeteccionesPorEmpresaQuery(Guid EmpresaId) : IRequest<Result<IReadOnlyList<DeteccionTrabajadorDto>>>;

/// <param name="AsignacionesActivas">
/// Solo tiene sentido para <see cref="Trabajadores.TipoDeteccion.Ausente"/>: cuántas
/// asignaciones activas de <see cref="TrabajadorExistenteId"/> cerraría
/// <c>ResolverDeteccionAusenteCommand</c> si se confirma la baja — mismo
/// criterio (<c>FechaBaja == null</c>) que <c>CierreDeAsignaciones.PorTrabajadorEliminadoAsync</c>.
/// Siempre 0 para <see cref="Trabajadores.TipoDeteccion.Nuevo"/>, que no tiene trabajador existente.
/// </param>
public record DeteccionTrabajadorDto(
    Guid Id, TipoDeteccion Tipo, string Nombre, string Apellidos, string Dni, Guid? TrabajadorExistenteId, DateTime CreadaEnUtc,
    int AsignacionesActivas);

public class ObtenerDeteccionesPorEmpresaQueryHandler(
    ITrabajadoresQueryContext dbContext, IAsignacionesQueryContext asignacionesContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDeteccionesPorEmpresaQuery, Result<IReadOnlyList<DeteccionTrabajadorDto>>>
{
    public async Task<Result<IReadOnlyList<DeteccionTrabajadorDto>>> Handle(
        ObtenerDeteccionesPorEmpresaQuery request, CancellationToken cancellationToken)
    {
        // Alcance de GESTIÓN, no de lectura (REC-149): las detecciones son una
        // herramienta de conciliación interna de personal (altas/bajas de la
        // Empresa entera, sin relación con un Cliente concreto) que alimenta
        // ResolverDeteccionAusenteCommand/ResolverDeteccionNuevoCommand, e
        // incluyen el DNI de cada trabajador detectado. No es documentación
        // de cumplimiento en la relación con el propio Cliente — es
        // información operativa de personal de la contratista, que un
        // usuario de portal (rol Cliente) no debería poder consultar solo
        // por tener a esa Empresa en su cartera de lectura.
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return Result.Fallo<IReadOnlyList<DeteccionTrabajadorDto>>(Error.Crear(
                "Empresa.NoEncontrada", "No encontramos esta empresa."));

        var detecciones = await dbContext.DeteccionesTrabajador
            .Where(d => d.EmpresaId == request.EmpresaId && !d.Resuelta)
            .OrderBy(d => d.Tipo).ThenBy(d => d.Apellidos)
            .Select(d => new { d.Id, d.Tipo, d.Nombre, d.Apellidos, d.Dni, d.TrabajadorExistenteId, d.CreadaEnUtc })
            .ToListAsync(cancellationToken);

        var trabajadorIds = detecciones
            .Where(d => d.TrabajadorExistenteId != null)
            .Select(d => d.TrabajadorExistenteId!.Value)
            .ToList();

        // Mismo criterio que ObtenerActivasPorTrabajadorAsync/CierreDeAsignaciones:
        // FechaBaja == null. Una sola consulta agrupada para todas las
        // detecciones Ausente de la página, en vez de una por fila.
        var asignacionesActivasPorTrabajador = trabajadorIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await asignacionesContext.Asignaciones
                .Where(a => a.FechaBaja == null && trabajadorIds.Contains(a.TrabajadorId))
                .GroupBy(a => a.TrabajadorId)
                .Select(g => new { TrabajadorId = g.Key, Cuenta = g.Count() })
                .ToDictionaryAsync(x => x.TrabajadorId, x => x.Cuenta, cancellationToken);

        var resultado = detecciones
            .Select(d => new DeteccionTrabajadorDto(
                d.Id, d.Tipo, d.Nombre, d.Apellidos, d.Dni, d.TrabajadorExistenteId, d.CreadaEnUtc,
                d.TrabajadorExistenteId is { } trabajadorId && asignacionesActivasPorTrabajador.TryGetValue(trabajadorId, out var cuenta) ? cuenta : 0))
            .ToList();

        return Result.Exito<IReadOnlyList<DeteccionTrabajadorDto>>(resultado);
    }
}
