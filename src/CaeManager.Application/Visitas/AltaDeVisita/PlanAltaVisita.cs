using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Gestiones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.AltaDeVisita;

/// <summary>Un Trabajador de la Visita que no tiene Asignación Trabajador–Centro activa al Centro y quedará asignado al crearla.</summary>
public record AsignacionPrevistaDto(Guid TrabajadorId, string TrabajadorNombre);

/// <summary>Por qué nace una Gestión al crear la Visita.</summary>
public enum MotivoGestionPrevista
{
    /// <summary>El Trabajador no tiene ningún Documento operativo de ese Tipo.</summary>
    Falta = 0,

    /// <summary>Tiene Documento, pero ninguno vale en este Centro hasta la fecha de fin de la Visita.</summary>
    CaducaAntesDelFin = 1,
}

/// <summary>Una Gestión que nacerá al crear la Visita: una por Trabajador y Tipo de documento, nunca una por Documento.</summary>
public record GestionPrevistaDto(
    Guid TrabajadorId, string TrabajadorNombre, Guid TipoDocumentoId, string TipoDocumentoNombre, MotivoGestionPrevista Motivo);

/// <summary>
/// Lo que pasará al crear una Visita (decisión de Chris, 2026-11-19, DECISION_LOG «Visita que asigna al Centro y dispara
/// Gestiones»): qué Trabajadores quedan asignados al Centro y qué Gestiones nacen. La vista previa y el Command lo calculan con
/// <see cref="IPlanAltaVisitaService"/>, para que la interfaz no pueda prometer una cosa y el servidor hacer otra.
///
/// <para>
/// <see cref="Solapadas"/>: Trabajadores sin Asignación activa al Centro a los que no se puede asignar hoy, porque tienen una
/// Asignación cerrada del mismo par cuyo periodo se solapa con hoy (DEC-19). No están en <see cref="Asignaciones"/>, y mientras
/// haya alguno el Command rechaza la Visita entera (<see cref="AltaDeVisitaConAsignaciones.CodigoSolape"/>) nombrándolos.
/// </para>
/// </summary>
public record PlanAltaVisitaDto(
    IReadOnlyList<AsignacionPrevistaDto> Asignaciones, IReadOnlyList<GestionPrevistaDto> Gestiones,
    IReadOnlyList<AsignacionPrevistaDto> Solapadas)
{
    public static PlanAltaVisitaDto Vacio { get; } = new([], [], []);
}

/// <summary>
/// Un Trabajador de la Visita visto desde la regla: si ya está asignado al Centro, si una Asignación cerrada del par impide
/// asignarlo hoy (<see cref="ReglaGestionesDeVisita.SituacionAsignacion"/>) y qué se le exige de serie.
/// </summary>
public readonly record struct TrabajadorEnPlan(
    Guid TrabajadorId, string Nombre, TipoContratacion TipoContratacion, bool AsignadoAlCentro, bool SolapaConAsignacionCerrada = false);

/// <summary>Una Asignación Trabajador–Centro del par, activa o cerrada, tal como está confirmada en la base.</summary>
public readonly record struct AsignacionEnPlan(DateOnly FechaAlta, DateOnly? FechaBaja);

/// <summary>Un Tipo de documento de ámbito Trabajador, sin archivar, con lo que decide si se exige de serie.</summary>
public readonly record struct TipoEnPlan(Guid TipoDocumentoId, string Nombre, bool ObligatorioPorDefecto, TiposContratacion TiposContratacionDeSerie);

/// <summary>Un Documento operativo de un Trabajador de la Visita; una renuncia también lo es.</summary>
public readonly record struct DocumentoEnPlan(Guid TrabajadorId, Guid TipoDocumentoId, DocumentoParaAcceso Documento);

/// <summary>
/// <b>Punto único</b> de la regla «qué Gestiones nacen al crear una Visita»: por cada Trabajador de la Visita y cada Tipo de
/// documento de ámbito Trabajador que el Centro le exige, una Gestión si no tiene ningún Documento de ese Tipo o si ninguno
/// vale en este Centro hasta la fecha de fin de la Visita.
///
/// <list type="bullet">
/// <item><b>Qué exige el Centro</b>: la regla existente, <see cref="ResolucionTipoDocumentoCentro"/> (fila explícita del Centro, o
/// el criterio global del Tipo filtrado por la serie del tipo de contratación del Trabajador), y nada si el Centro no tiene gestión
/// CAE (P1-X2). Un Tipo archivado no da Gestión: no admite altas nuevas, así que no se podría cumplir (mismo filtro que
/// <c>EvaluacionDeAccesoPorCentroService</c>).</item>
/// <item><b>Falta</b>: el Trabajador no tiene ningún Documento operativo de ese Tipo. Una renuncia operativa cuenta como Documento
/// existente, igual que en el detalle de la Visita (<c>ObtenerDocumentacionVisitaQuery</c> solo pinta «Faltante» cuando no hay
/// ningún Documento operativo del Tipo), y vale según su propia vigencia.</item>
/// <item><b>Caduca antes del fin</b>: el vencimiento efectivo en este Centro (<see cref="ReglaBloqueoDeAcceso.VencimientoEfectivo"/>,
/// con su periodicidad especial) es anterior a la fecha de fin. La tolerancia no cuenta: Chris habló de lo que caduca, no de lo
/// que deja de permitir el acceso. «No caduca» y «Sin confirmar» sin periodicidad del Centro no vencen.</item>
/// <item><b>Sin duplicar</b>: no nace una Gestión si ya hay una Pendiente del mismo Trabajador, Centro y Tipo.</item>
/// </list>
/// Función pura: quien la usa trae los datos y la evalúa en memoria.
/// </summary>
public static class ReglaGestionesDeVisita
{
    /// <summary>
    /// Si un Trabajador ya está asignado al Centro y, si no, si una Asignación cerrada del par impide asignarlo con alta
    /// <paramref name="hoy"/>. Misma semántica que <c>CrearAsignacionesCommandHandler.PlanificarAsync</c> (DEC-19): una fila
    /// activa es «asignado»; una cerrada solapa si su rango no es vacío (<c>FechaBaja != FechaAlta</c>) y el alta nueva, un rango
    /// abierto desde hoy, empieza antes de su baja exclusiva (<c>hoy &lt; FechaBaja</c>). Estar asignado manda sobre solapar.
    /// </summary>
    public static (bool Asignado, bool Solapa) SituacionAsignacion(IEnumerable<AsignacionEnPlan> filasDelPar, DateOnly hoy)
    {
        var solapa = false;
        foreach (var fila in filasDelPar)
        {
            if (fila.FechaBaja is not { } baja)
                return (true, false);
            if (baja != fila.FechaAlta && hoy < baja)
                solapa = true;
        }

        return (false, solapa);
    }

    public static PlanAltaVisitaDto Calcular(
        IReadOnlyList<TrabajadorEnPlan> trabajadores,
        IReadOnlyList<TipoEnPlan> tipos,
        IReadOnlyDictionary<(Guid TipoDocumentoId, Guid CentroId), bool> incluidoPorPar,
        IReadOnlyDictionary<Guid, int?> periodicidadEspecialPorTipo,
        IReadOnlyList<DocumentoEnPlan> documentos,
        IReadOnlySet<(Guid TrabajadorId, Guid TipoDocumentoId)> gestionesPendientes,
        Guid centroId, bool centroConGestionCae, DateOnly fechaFin)
    {
        var asignaciones = trabajadores
            .Where(t => !t.AsignadoAlCentro && !t.SolapaConAsignacionCerrada)
            .Select(t => new AsignacionPrevistaDto(t.TrabajadorId, t.Nombre))
            .ToList();
        var solapadas = trabajadores
            .Where(t => !t.AsignadoAlCentro && t.SolapaConAsignacionCerrada)
            .Select(t => new AsignacionPrevistaDto(t.TrabajadorId, t.Nombre))
            .ToList();

        if (!centroConGestionCae)
            return new PlanAltaVisitaDto(asignaciones, [], solapadas);

        var documentosPorPar = documentos.ToLookup(d => (d.TrabajadorId, d.TipoDocumentoId), d => d.Documento);
        var gestiones = new List<GestionPrevistaDto>();

        foreach (var trabajador in trabajadores)
        {
            foreach (var tipo in tipos)
            {
                if (!ResolucionTipoDocumentoCentro.Aplica(
                        incluidoPorPar, tipo.TipoDocumentoId, centroId, tipo.ObligatorioPorDefecto,
                        tipo.TiposContratacionDeSerie, trabajador.TipoContratacion))
                    continue;

                if (gestionesPendientes.Contains((trabajador.TrabajadorId, tipo.TipoDocumentoId)))
                    continue;

                var delPar = documentosPorPar[(trabajador.TrabajadorId, tipo.TipoDocumentoId)].ToList();
                MotivoGestionPrevista? motivo = null;
                if (delPar.Count == 0)
                {
                    motivo = MotivoGestionPrevista.Falta;
                }
                else
                {
                    var periodicidad = periodicidadEspecialPorTipo.GetValueOrDefault(tipo.TipoDocumentoId);
                    var cubreLaVisita = delPar.Any(d =>
                        ReglaBloqueoDeAcceso.VencimientoEfectivo(d, periodicidad) is not { } vence || vence >= fechaFin);
                    if (!cubreLaVisita)
                        motivo = MotivoGestionPrevista.CaducaAntesDelFin;
                }

                if (motivo is { } m)
                    gestiones.Add(new GestionPrevistaDto(trabajador.TrabajadorId, trabajador.Nombre, tipo.TipoDocumentoId, tipo.Nombre, m));
            }
        }

        return new PlanAltaVisitaDto(
            asignaciones,
            gestiones.OrderBy(g => g.TrabajadorNombre, StringComparer.CurrentCulture)
                .ThenBy(g => g.TipoDocumentoNombre, StringComparer.CurrentCulture)
                .ToList(),
            solapadas);
    }
}

/// <summary>
/// La autoridad para dar de alta las Asignaciones Trabajador–Centro de una Visita: la misma de <c>CrearAsignacionCommand</c>
/// (<see cref="IAutoridadAsignacionesService"/>), sobre el Centro y sobre cada Trabajador que hay que asignar. Ver un
/// Trabajador no basta para asignarlo (auditoría Módulo 5, hallazgo crítico 6/9). Sin nadie que asignar no hace falta.
/// </summary>
public static class AutoridadParaAsignar
{
    public static async Task<bool> TieneAsync(
        IAutoridadAsignacionesService autoridad, Guid centroId, IReadOnlyList<Guid> trabajadorIdsPorAsignar,
        CancellationToken cancellationToken)
    {
        if (trabajadorIdsPorAsignar.Count == 0)
            return true;

        if (!await autoridad.PuedeModificarAsignacionesDelCentroAsync(centroId, cancellationToken))
            return false;

        var conAutoridad = await autoridad.FiltrarTrabajadoresConAutoridadAsync(trabajadorIdsPorAsignar, cancellationToken);
        return trabajadorIdsPorAsignar.All(conAutoridad.Contains);
    }
}

/// <summary>
/// Carga los datos de <see cref="ReglaGestionesDeVisita"/> y la aplica. No decide autorización ni alcance: quien lo llama ya
/// comprobó el Centro (<c>CentroParaGestionVisibleAsync</c>) y los Trabajadores (<see cref="TrabajadoresAdmisiblesEnVisita"/>).
/// Todas las lecturas van por contextos acotados al Tenant efectivo (filtro de consulta y RLS).
/// </summary>
public interface IPlanAltaVisitaService
{
    /// <param name="hoy">
    /// La fecha de alta de las Asignaciones que se crearían, con la que se mide el solape DEC-19: <c>DiaDeNegocio.Hoy()</c>, el
    /// mismo valor que usa <see cref="AltaDeVisitaConAsignaciones"/> para darlas de alta.
    /// </param>
    Task<PlanAltaVisitaDto> CalcularAsync(
        Guid centroId, DateOnly fechaFin, DateOnly hoy, IReadOnlyCollection<Guid> trabajadorIds, CancellationToken cancellationToken);
}

public class PlanAltaVisitaService(
    ICentrosQueryContext centrosContext,
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDocumentosQueryContext documentosContext,
    IGestionesQueryContext gestionesContext)
    : IPlanAltaVisitaService
{
    public async Task<PlanAltaVisitaDto> CalcularAsync(
        Guid centroId, DateOnly fechaFin, DateOnly hoy, IReadOnlyCollection<Guid> trabajadorIds, CancellationToken cancellationToken)
    {
        var ids = trabajadorIds.Distinct().ToList();
        if (ids.Count == 0)
            return PlanAltaVisitaDto.Vacio;

        // Todas las filas del par, activas y cerradas: DEC-19 mide el solape también contra las cerradas, como
        // CrearAsignacionesCommandHandler. Proyección: lo confirmado en la base, no lo rastreado.
        var asignacionesDelPar = (await asignacionesContext.Asignaciones
            .Where(a => a.CentroId == centroId && ids.Contains(a.TrabajadorId))
            .Select(a => new { a.TrabajadorId, a.FechaAlta, a.FechaBaja })
            .ToListAsync(cancellationToken))
            .ToLookup(a => a.TrabajadorId, a => new AsignacionEnPlan(a.FechaAlta, a.FechaBaja));

        var trabajadores = (await trabajadoresContext.Trabajadores
            .Where(t => ids.Contains(t.Id))
            .OrderBy(t => t.Apellidos).ThenBy(t => t.Nombre)
            .Select(t => new { t.Id, Nombre = t.Nombre + " " + t.Apellidos, t.TipoContratacion })
            .ToListAsync(cancellationToken))
            .Select(t =>
            {
                var (asignado, solapa) = ReglaGestionesDeVisita.SituacionAsignacion(asignacionesDelPar[t.Id], hoy);
                return new TrabajadorEnPlan(t.Id, t.Nombre, t.TipoContratacion, asignado, solapa);
            })
            .ToList();

        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(centrosContext, [centroId], cancellationToken);
        var centroConGestionCae = !sinGestionCae.Contains(centroId);
        if (!centroConGestionCae)
        {
            return ReglaGestionesDeVisita.Calcular(
                trabajadores, [], new Dictionary<(Guid, Guid), bool>(), new Dictionary<Guid, int?>(), [],
                new HashSet<(Guid, Guid)>(), centroId, centroConGestionCae: false, fechaFin);
        }

        // Un Tipo archivado no admite altas nuevas: una Gestión suya no se podría cumplir (EvaluacionDeAccesoPorCentroService).
        var tipos = (await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador && !t.Archivado)
            .Select(t => new { t.Id, t.Nombre, Obligatorio = t.Requerido == RequisitoDocumental.Si, t.TiposContratacionDeSerie })
            .ToListAsync(cancellationToken))
            .Select(t => new TipoEnPlan(t.Id, t.Nombre, t.Obligatorio, t.TiposContratacionDeSerie))
            .ToList();
        var tipoIds = tipos.Select(t => t.TipoDocumentoId).ToList();

        // Proyección: lo confirmado en la base, no una instancia rastreada (revisión Codex de la PR #2074, ronda 4).
        var filasDelCentro = await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tc.CentroId == centroId && tipoIds.Contains(tc.TipoDocumentoId))
            .Select(tc => new { tc.TipoDocumentoId, tc.Incluido, tc.PeriodicidadEspecialMeses })
            .ToListAsync(cancellationToken);

        // Una renuncia operativa es un Documento existente, como en el detalle de la Visita: no se filtra.
        var documentos = (await documentosContext.Documentos.Operativos()
            .Where(d => d.TrabajadorId != null
                && ids.Contains(d.TrabajadorId!.Value)
                && tipoIds.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision })
            .ToListAsync(cancellationToken))
            .Select(d => new DocumentoEnPlan(d.TrabajadorId, d.TipoDocumentoId,
                new DocumentoParaAcceso(VigenciaDocumento.Rehidratar(d.EstadoVigencia, d.FechaVencimiento), d.FechaEmision)))
            .ToList();

        var pendientes = (await gestionesContext.Gestiones
            .Where(g => g.CentroId == centroId && g.Estado == EstadoGestion.Pendiente && ids.Contains(g.TrabajadorId))
            .Select(g => new { g.TrabajadorId, g.TipoDocumentoId })
            .ToListAsync(cancellationToken))
            .Select(g => (g.TrabajadorId, g.TipoDocumentoId))
            .ToHashSet();

        return ReglaGestionesDeVisita.Calcular(
            trabajadores, tipos,
            filasDelCentro.ToDictionary(f => (f.TipoDocumentoId, centroId), f => f.Incluido),
            filasDelCentro.ToDictionary(f => f.TipoDocumentoId, f => f.PeriodicidadEspecialMeses),
            documentos, pendientes, centroId, centroConGestionCae: true, fechaFin);
    }
}
