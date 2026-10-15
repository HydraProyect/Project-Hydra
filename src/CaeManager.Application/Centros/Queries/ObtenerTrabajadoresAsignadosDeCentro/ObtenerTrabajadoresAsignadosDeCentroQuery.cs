using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerTrabajadoresAsignadosDeCentro;

/// <summary>
/// Respalda la pestaña "Trabajadores" del Context Workspace de Centro y el filtro «relacionados» de Visitas.
/// Son dos orígenes: las Asignaciones activas al Centro (FechaBaja null) y quien participa (ProyectoTecnico
/// activo) en un evento de ese Centro que no ha terminado (ni cerrado ni pasada su fecha de fin). Quien solo
/// entró por eventos ya terminados no aparece: el Centro no acumula a los voluntarios que pasaron por él. Un
/// Trabajador con Asignación activa aparece una sola vez, como asignado.
/// </summary>
public record ObtenerTrabajadoresAsignadosDeCentroQuery(Guid CentroId) : IRequest<IReadOnlyList<TrabajadorAsignadoDto>>;

/// <param name="EventoNombre">null si entra por Asignación al Centro; el nombre del evento si solo entra por él.</param>
public record TrabajadorAsignadoDto(Guid TrabajadorId, string Nombre, string EmpleadorNombre, DateOnly FechaAlta, string? EventoNombre = null);

public class ObtenerTrabajadoresAsignadosDeCentroQueryHandler(
    IAsignacionesQueryContext asignacionesContext, IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext,
    IProyectosQueryContext proyectosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerTrabajadoresAsignadosDeCentroQuery, IReadOnlyList<TrabajadorAsignadoDto>>
{
    public async Task<IReadOnlyList<TrabajadorAsignadoDto>> Handle(
        ObtenerTrabajadoresAsignadosDeCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return [];

        var asignados = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.CentroId == request.CentroId && asignacion.FechaBaja == null
            select new { asignacion.TrabajadorId, asignacion.FechaAlta, EventoNombre = (string?)null })
            .ToListAsync(cancellationToken);

        // Misma expresión que Proyecto.EventoTerminadoEn, negada y traducible a SQL.
        var hoy = DiaDeNegocio.Hoy();
        var porEventos = await (
            from proyecto in proyectosContext.Proyectos
            where proyecto.CentroId == request.CentroId && proyecto.Tipo == TipoProyecto.Evento
                && proyecto.FechaCierreReal == null && proyecto.FechaFinPrevista >= hoy
            join participante in proyectosContext.ProyectosTecnicos on proyecto.Id equals participante.ProyectoId
            where participante.FechaBaja == null
            orderby proyecto.FechaInicio
            select new { participante.TrabajadorId, participante.FechaAlta, EventoNombre = (string?)proyecto.Nombre })
            .ToListAsync(cancellationToken);

        var asignadosIds = asignados.Select(a => a.TrabajadorId).ToHashSet();
        var filas = asignados
            .Concat(porEventos.Where(e => !asignadosIds.Contains(e.TrabajadorId)).DistinctBy(e => e.TrabajadorId))
            .ToList();
        if (filas.Count == 0)
            return [];

        var trabajadorIds = filas.Select(f => f.TrabajadorId).Distinct().ToList();
        var trabajadores = await (
            from trabajador in trabajadoresContext.Trabajadores
            where trabajadorIds.Contains(trabajador.Id)
            join empresa in empresasContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in empresasContext.Empresas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            select new
            {
                trabajador.Id,
                trabajador.Nombre,
                NombreCompleto = trabajador.Nombre + " " + trabajador.Apellidos,
                EmpleadorNombre = empresa != null ? empresa.RazonSocial : subcontrata!.RazonSocial,
            })
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        // El join con Trabajadores aplica sus filtros (Tenant propietario, baja lógica), como antes.
        return filas
            .Where(f => trabajadores.ContainsKey(f.TrabajadorId))
            .Select(f => (Fila: f, Trabajador: trabajadores[f.TrabajadorId]))
            .OrderBy(x => x.Trabajador.Nombre, StringComparer.CurrentCulture)
            .Select(x => new TrabajadorAsignadoDto(
                x.Fila.TrabajadorId, x.Trabajador.NombreCompleto, x.Trabajador.EmpleadorNombre, x.Fila.FechaAlta, x.Fila.EventoNombre))
            .ToList();
    }
}
