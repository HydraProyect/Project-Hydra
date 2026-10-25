using CaeManager.Application.Asignaciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros;

/// <summary>Un requisito obligatorio de un Centro (<see cref="TipoDocumentoCentro.BloqueaAcceso"/>) que su sujeto incumple hoy.</summary>
public readonly record struct RequisitoObligatorioIncumplido(
    Guid CentroId, Guid TipoDocumentoId, string TipoDocumentoNombre, SituacionDeRequisitoBloqueante Situacion);

/// <summary>
/// <b>Punto único</b> de la regla «qué pone en rojo el estado de una Empresa o de un Trabajador» (decisión del propietario del
/// producto, 2026-10-23): cada nivel pinta solo su contexto, y para el rojo cuenta solo lo que bloquea el acceso.
///
/// <list type="bullet">
/// <item><b>Empresa.</b> Su estado sale en rojo si un Centro donde opera (donde tiene Trabajadores con Asignación activa)
/// marca como obligatorio un documento de ámbito Empresa y ese documento falta o ya no vale allí. Nunca por lo que les
/// falta a sus Trabajadores.</item>
/// <item><b>Trabajador.</b> Su estado sale en rojo si un Centro donde está asignado marca como obligatorio un documento de
/// ámbito Trabajador y ese documento falta o ya no vale allí. Nunca por un documento de su Empresa: el bloqueo de un Centro
/// por el acta de la Empresa no pinta en rojo a todos sus Trabajadores.</item>
/// <item><b>Obligatorio</b> es <see cref="TipoDocumentoCentro.BloqueaAcceso"/>. Lo exigido sin esa marca no bloquea el Centro y
/// no genera alarma, falte o no.</item>
/// <item><b>Por Centro.</b> Se evalúa con <see cref="ReglaBloqueoDeAcceso"/> en cada Centro, con sus condiciones (vigencia
/// propia y tolerancia), a través de <see cref="IEvaluacionDeAccesoPorCentroService"/>: no hay otra copia de la regla. Lo que
/// incumple en el Centro A no cambia nada en el Centro B.</item>
/// <item><b>Qué incumple:</b> <see cref="SituacionDeRequisitoBloqueante.Ausente"/> y <see cref="SituacionDeRequisitoBloqueante.Vencido"/>
/// (<see cref="CuentaParaElEstado"/>). El pendiente en la plataforma CAE del Centro bloquea a personas en ese Centro, pero el
/// documento vale en TALVEG: no tiñe el estado general. La acreditación rechazada por la plataforma es causa del Centro (D-7) y
/// no llega aquí.</item>
/// </list>
///
/// <para>
/// Alcance: solo los Centros visibles para el usuario (lo aplica <see cref="IEvaluacionDeAccesoPorCentroService"/>). Un usuario
/// que no alcanza un Centro no aprende por el estado de una Empresa qué exige ese Centro. RLS no cambia. La única excepción
/// es el estado materializado (<see cref="IEvaluacionDeAccesoDelTenant"/>), que guarda el estado del Tenant entero; su lector
/// (<c>ILectorEstadoMaterializado</c>) solo lo sirve a quien alcanza todos los Centros del Tenant y calcula con esta clase a
/// los demás.
/// </para>
/// </summary>
public sealed record RequisitosObligatoriosIncumplidos(
    IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>> PorEmpresa,
    IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>> PorTrabajador)
{
    public static RequisitosObligatoriosIncumplidos Ninguno { get; } = new(
        new Dictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>(),
        new Dictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>());

    /// <summary>Qué situaciones de un requisito obligatorio ponen en rojo el estado de su sujeto: ausente o vencido.</summary>
    public static bool CuentaParaElEstado(SituacionDeRequisitoBloqueante situacion) =>
        situacion is SituacionDeRequisitoBloqueante.Ausente or SituacionDeRequisitoBloqueante.Vencido;

    /// <summary>
    /// Agrupa los requisitos ya evaluados por Centro. Un requisito de Empresa se evalúa una vez por cada Trabajador de la
    /// Empresa asignado al Centro (<see cref="CalculoBloqueoDeAccesoDeTrabajadores"/>): aquí cuenta una sola vez por
    /// (Empresa, Centro, Tipo).
    /// </summary>
    public static RequisitosObligatoriosIncumplidos Desde(
        IEnumerable<RequisitoEvaluado> requisitos, IReadOnlyDictionary<Guid, string> nombresDeTipo)
    {
        var incumplidos = requisitos.Where(r => CuentaParaElEstado(r.Resultado.Situacion)).ToList();

        RequisitoObligatorioIncumplido Incumplido(RequisitoEvaluado r) => new(
            r.CentroId, r.TipoDocumentoId, nombresDeTipo.GetValueOrDefault(r.TipoDocumentoId) ?? string.Empty, r.Resultado.Situacion);

        var porEmpresa = incumplidos
            .Where(r => r.Ambito == AmbitoAplicacion.Empresa && r.EmpresaId is not null)
            .GroupBy(r => r.EmpresaId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<RequisitoObligatorioIncumplido>)g.DistinctBy(r => (r.CentroId, r.TipoDocumentoId)).Select(Incumplido).ToList());

        var porTrabajador = incumplidos
            .Where(r => r.Ambito == AmbitoAplicacion.Trabajador)
            .GroupBy(r => r.TrabajadorId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<RequisitoObligatorioIncumplido>)g.DistinctBy(r => (r.CentroId, r.TipoDocumentoId)).Select(Incumplido).ToList());

        return new RequisitosObligatoriosIncumplidos(porEmpresa, porTrabajador);
    }

    /// <summary>Algún requisito obligatorio tiene documento, pero ya no vale en su Centro: el estado es Vencido, en rojo.</summary>
    public static bool HayVencido(IReadOnlyList<RequisitoObligatorioIncumplido> incumplidos) =>
        incumplidos.Any(i => i.Situacion == SituacionDeRequisitoBloqueante.Vencido);

    /// <summary>Algún requisito obligatorio no tiene ningún documento: el estado es Pendiente, en rojo.</summary>
    public static bool HayAusente(IReadOnlyList<RequisitoObligatorioIncumplido> incumplidos) =>
        incumplidos.Any(i => i.Situacion == SituacionDeRequisitoBloqueante.Ausente);

    /// <summary>Los nombres de los Tipos que faltan (sin ningún documento), sin repetir y en orden alfabético: el motivo «Falta: …».</summary>
    public static IReadOnlyList<string> NombresAusentes(IReadOnlyList<RequisitoObligatorioIncumplido> incumplidos) =>
        incumplidos
            .Where(i => i.Situacion == SituacionDeRequisitoBloqueante.Ausente && i.TipoDocumentoNombre.Length > 0)
            .Select(i => i.TipoDocumentoNombre)
            .Distinct()
            .Order(StringComparer.CurrentCulture)
            .ToList();
}

/// <summary>
/// Carga <see cref="RequisitosObligatoriosIncumplidos"/> para un conjunto de Empresas o de Trabajadores: limita la evaluación a
/// los Centros donde operan y deja el resto (Centros visibles, gestión CAE, tolerancia, renuncias) a
/// <see cref="IEvaluacionDeAccesoPorCentroService"/>. Lo leen el listado de Empresas y <c>ObtenerTrabajadoresQuery</c> (listado de
/// Trabajadores, Empresa 360 y la cabecera de Trabajador 360), para que ninguna pantalla recalcule el rojo.
/// </summary>
public interface IRequisitosObligatoriosIncumplidosService
{
    /// <summary>Los requisitos obligatorios de ámbito Empresa incumplidos de esas Empresas, por Empresa.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>> DeEmpresasAsync(
        IQueryable<Guid> empresaIds, CancellationToken cancellationToken);

    /// <summary>Los requisitos obligatorios de ámbito Trabajador incumplidos de esos Trabajadores, por Trabajador.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>> DeTrabajadoresAsync(
        IQueryable<Guid> trabajadorIds, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="DeEmpresasAsync"/> y <see cref="DeTrabajadoresAsync"/> a la vez, con una sola evaluación de los Centros donde
    /// operan unos y otros: el listado de Subcontratas pide el estado de la Subcontrata y el de sus Trabajadores, que trabajan en
    /// los mismos Centros. El resultado de cada lado es el mismo que el de su método por separado.
    /// </summary>
    Task<RequisitosObligatoriosIncumplidos> DeEmpresasYTrabajadoresAsync(
        IQueryable<Guid> empresaIds, IQueryable<Guid> trabajadorIds, CancellationToken cancellationToken);
}

public class RequisitosObligatoriosIncumplidosService(
    IEvaluacionDeAccesoPorCentroService evaluacionDeAcceso,
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ITiposDocumentoQueryContext tiposDocumentoContext)
    : IRequisitosObligatoriosIncumplidosService
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>> DeEmpresasAsync(
        IQueryable<Guid> empresaIds, CancellationToken cancellationToken)
    {
        var actividad = await ActividadDeEmpresasAsync(empresaIds, cancellationToken);
        var incumplidos = await EvaluarAsync(actividad.Select(a => a.CentroId).Distinct().ToList(), cancellationToken);
        return DeLasEmpresas(incumplidos, actividad);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>> DeTrabajadoresAsync(
        IQueryable<Guid> trabajadorIds, CancellationToken cancellationToken)
    {
        var asignaciones = await AsignacionesDeTrabajadoresAsync(trabajadorIds, cancellationToken);
        if (asignaciones.Count == 0)
            return RequisitosObligatoriosIncumplidos.Ninguno.PorTrabajador;

        var incumplidos = await EvaluarAsync(asignaciones.Select(a => a.CentroId).Distinct().ToList(), cancellationToken);
        return DeLosTrabajadores(incumplidos, asignaciones);
    }

    public async Task<RequisitosObligatoriosIncumplidos> DeEmpresasYTrabajadoresAsync(
        IQueryable<Guid> empresaIds, IQueryable<Guid> trabajadorIds, CancellationToken cancellationToken)
    {
        var actividad = await ActividadDeEmpresasAsync(empresaIds, cancellationToken);
        var asignaciones = await AsignacionesDeTrabajadoresAsync(trabajadorIds, cancellationToken);

        // Cada requisito se evalúa por Centro (ReglaBloqueoDeAcceso), así que evaluar la unión de Centros da a cada lado lo mismo
        // que su propia evaluación: lo que incumple en un Centro no cambia nada en otro.
        var incumplidos = await EvaluarAsync(
            actividad.Select(a => a.CentroId).Concat(asignaciones.Select(a => a.CentroId)).Distinct().ToList(), cancellationToken);
        return new RequisitosObligatoriosIncumplidos(DeLasEmpresas(incumplidos, actividad), DeLosTrabajadores(incumplidos, asignaciones));
    }

    /// <summary>
    /// Dónde opera cada Empresa: los Centros donde trabajan sus Trabajadores (los propios y, en una Subcontrata, los suyos), el
    /// mismo universo que el porcentaje de la Empresa. Una Empresa sin Asignaciones activas no opera en ningún Centro y no puede
    /// incumplir nada.
    /// </summary>
    private async Task<List<(Guid CentroId, Guid? EmpresaId)>> ActividadDeEmpresasAsync(
        IQueryable<Guid> empresaIds, CancellationToken cancellationToken) =>
        (await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where (trabajador.EmpresaId != null && empresaIds.Contains(trabajador.EmpresaId.Value))
                  || (trabajador.SubcontrataId != null && empresaIds.Contains(trabajador.SubcontrataId.Value))
            select new { asignacion.CentroId, EmpresaId = trabajador.EmpresaId ?? trabajador.SubcontrataId })
            .Distinct()
            .ToListAsync(cancellationToken))
        .Select(a => (a.CentroId, a.EmpresaId))
        .ToList();

    private async Task<List<(Guid TrabajadorId, Guid CentroId)>> AsignacionesDeTrabajadoresAsync(
        IQueryable<Guid> trabajadorIds, CancellationToken cancellationToken) =>
        (await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && trabajadorIds.Contains(a.TrabajadorId))
            .Select(a => new { a.TrabajadorId, a.CentroId })
            .Distinct()
            .ToListAsync(cancellationToken))
        .Select(a => (a.TrabajadorId, a.CentroId))
        .ToList();

    private static IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>> DeLasEmpresas(
        RequisitosObligatoriosIncumplidos incumplidos, List<(Guid CentroId, Guid? EmpresaId)> actividad)
    {
        var pedidas = actividad.Select(a => a.EmpresaId).ToHashSet();
        return incumplidos.PorEmpresa.Where(p => pedidas.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>> DeLosTrabajadores(
        RequisitosObligatoriosIncumplidos incumplidos, List<(Guid TrabajadorId, Guid CentroId)> asignaciones)
    {
        var pedidos = asignaciones.Select(a => a.TrabajadorId).ToHashSet();
        return incumplidos.PorTrabajador.Where(p => pedidos.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
    }

    private async Task<RequisitosObligatoriosIncumplidos> EvaluarAsync(
        IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return RequisitosObligatoriosIncumplidos.Ninguno;

        var evaluacion = await evaluacionDeAcceso.EvaluarAsync(centroIds, cancellationToken);
        var tipoIds = evaluacion.Requisitos
            .Where(r => RequisitosObligatoriosIncumplidos.CuentaParaElEstado(r.Resultado.Situacion))
            .Select(r => r.TipoDocumentoId)
            .Distinct()
            .ToList();
        if (tipoIds.Count == 0)
            return RequisitosObligatoriosIncumplidos.Ninguno;

        var nombres = await tiposDocumentoContext.TiposDocumento
            .Where(t => tipoIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        return RequisitosObligatoriosIncumplidos.Desde(evaluacion.Requisitos, nombres);
    }
}
