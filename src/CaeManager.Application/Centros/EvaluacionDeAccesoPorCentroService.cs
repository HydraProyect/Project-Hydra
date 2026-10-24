using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros;

/// <summary>Una Asignación activa evaluada: quién es el Trabajador y de qué Empresa.</summary>
/// <param name="EmpresaPropiaId"><c>Trabajador.EmpresaId</c>; <c>null</c> en un Trabajador de subcontratista (lo guarda <c>SubcontrataId</c>).</param>
public record AsignacionEvaluada(
    Guid CentroId, Guid TrabajadorId, string TrabajadorNombre, Guid? EmpresaPropiaId, Guid? EmpresaDelTrabajadorId);

/// <summary>Resultado de evaluar los requisitos bloqueantes de un conjunto de Centros: las Asignaciones consideradas y cada requisito evaluado.</summary>
public record EvaluacionDeAccesoPorCentro(
    IReadOnlyList<AsignacionEvaluada> Asignaciones, IReadOnlyList<RequisitoEvaluado> Requisitos)
{
    public static EvaluacionDeAccesoPorCentro Vacia { get; } = new([], []);
}

/// <summary>
/// Carga los datos y aplica <see cref="CalculoBloqueoDeAccesoDeTrabajadores"/> <b>por Centro</b>: es el único sitio que
/// decide qué filas son requisito bloqueante de un Centro, con qué vigencia propia y con qué tolerancia (la del Centro si la
/// personaliza; si no, la de su Cliente empresarial titular; si no, 0 — <see cref="ReglaBloqueoDeAcceso.ResolverToleranciaDias"/>),
/// y qué Asignaciones se consideran. Lo usan Mi trabajo (vía <c>ObtenerDocumentacionBloqueantePendienteQuery</c>) y el detalle
/// por Trabajador del Centro 360, para que ninguna superficie recalcule el bloqueo.
///
/// <para>
/// Desde el 2026-10-10 también aplica el Pendiente en la plataforma (<see cref="ReglaPendienteEnPlataforma"/>): un documento
/// que vale pero está sin subir o sin validar en la plataforma CAE de un Centro bloquea a su sujeto en ese Centro
/// (<see cref="SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma"/> o
/// <see cref="SituacionDeRequisitoBloqueante.SinValidarEnPlataforma"/>) <b>solo si el Centro marca ese tipo con
/// <c>BloqueaAcceso</c></b>, con la misma carga que pone el Centro en <c>EstadoCentro.Pendiente</c>
/// (<see cref="PendientesEnPlataformaDeCentros"/>). Un tipo pendiente que el Centro no marca como bloqueante pone el Centro en
/// «Pendiente» pero no bloquea a nadie.
/// </para>
///
/// <para>
/// Alcance: solo Centros visibles para el usuario (<see cref="IAlcanceDatosService"/>) y con gestión CAE (un Centro sin
/// gestión CAE no exige documentación a nadie: una fila <c>BloqueaAcceso</c> que quedó de cuando la exigía no bloquea nada,
/// P1-X2). El aislamiento entre Tenants lo da RLS: nunca se cruza de un Tenant a otro.
/// </para>
/// </summary>
public interface IEvaluacionDeAccesoPorCentroService
{
    /// <param name="centroIds">Limita la evaluación a estos Centros; <c>null</c> = todos los visibles.</param>
    Task<EvaluacionDeAccesoPorCentro> EvaluarAsync(IReadOnlyCollection<Guid>? centroIds, CancellationToken cancellationToken);
}

/// <summary>
/// La misma evaluación que <see cref="IEvaluacionDeAccesoPorCentroService"/>, sin el alcance del usuario: la usa solo el
/// estado materializado, que es dato del Tenant propietario y no de quien lo mira. RLS sigue acotando al Tenant de la
/// conexión. Ninguna pantalla debe llamarla: una pantalla que la usara enseñaría lo que exigen Centros que el usuario no
/// alcanza (lo congela <c>ReglasDeNegocioSinCopiasTests</c>).
/// <para>
/// Consecuencia para quien lea el estado materializado: el estado guardado de un Trabajador o de una Empresa es el de
/// <b>todos</b> los Centros del Tenant, el que vería un usuario sin Asignación de Cartera. Filtrar filas no basta para un
/// usuario con alcance restringido, porque es un agregado: su lector recompone el rojo desde los pares de
/// <c>EstadosRequisito</c> de los Centros que ve (<c>BloqueaAcceso</c> y <c>EstadoTalveg</c>), como pide
/// <see cref="RequisitosObligatoriosIncumplidos"/> (diseño del estado materializado, § 3.2 y § 3.4).
/// </para>
/// </summary>
public interface IEvaluacionDeAccesoDelTenant
{
    Task<EvaluacionDeAccesoPorCentro> EvaluarCentrosAsync(IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken);

    /// <summary>
    /// Todas las filas de requisitos de esos Centros (no solo las bloqueantes), con la misma resolución de condiciones que la
    /// evaluación: el estado materializado las necesita para saber qué pares cuentan para el cumplimiento, cuáles bloquean y
    /// cuándo deja un documento de valer en el Centro (<c>ProximoCambioEl</c>).
    /// </summary>
    Task<IReadOnlyList<CondicionDeRequisitoDelCentro>> CondicionesDeRequisitosAsync(
        IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken);
}

/// <summary>
/// Una fila de requisitos de un Centro: <see cref="BloqueaAcceso"/> con la condición de la evaluación (incluido, marcado, Tipo
/// sin archivar y de un ámbito que bloquea) y las condiciones de acceso resueltas como <c>VigenciaEnCentro.Condiciones</c>.
/// </summary>
public sealed record CondicionDeRequisitoDelCentro(
    Guid CentroId, Guid TipoDocumentoId, bool Incluido, bool BloqueaAcceso, CondicionesDeAccesoDelCentro Condiciones);

public class EvaluacionDeAccesoPorCentroService(
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    IDocumentosQueryContext documentosContext,
    IAlcanceDatosService alcanceDatos)
    : IEvaluacionDeAccesoPorCentroService, IEvaluacionDeAccesoDelTenant
{
    public async Task<EvaluacionDeAccesoPorCentro> EvaluarAsync(
        IReadOnlyCollection<Guid>? centroIds, CancellationToken cancellationToken) =>
        await EvaluarEnAlcanceAsync(centroIds, await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken), cancellationToken);

    public Task<EvaluacionDeAccesoPorCentro> EvaluarCentrosAsync(
        IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken) =>
        EvaluarEnAlcanceAsync(centroIds, centroIdsVisibles: null, cancellationToken);

    public async Task<IReadOnlyList<CondicionDeRequisitoDelCentro>> CondicionesDeRequisitosAsync(
        IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return [];

        var filas = await (
            from tc in tiposDocumentoContext.TiposDocumentoCentros
            where centroIds.Contains(tc.CentroId)
            join tipo in tiposDocumentoContext.TiposDocumento on tc.TipoDocumentoId equals tipo.Id
            select new
            {
                tc.CentroId,
                tc.TipoDocumentoId,
                tc.Incluido,
                tc.PeriodicidadEspecialMeses,
                tc.ToleranciaDias,
                // Misma condición que la carga de EvaluarEnAlcanceAsync: incluido, marcado, Tipo sin archivar y de un ámbito que bloquea.
                Bloquea = tc.Incluido && tc.BloqueaAcceso && !tipo.Archivado,
                tipo.AmbitoAplicacion
            })
            .ToListAsync(cancellationToken);
        if (filas.Count == 0)
            return [];

        var titularPorCentro = await TitularesDeCentrosAsync(filas.Select(f => f.CentroId).Distinct().ToList(), cancellationToken);
        var toleranciasDeCliente = await VigenciaEnCentro.CargarToleranciasDeClientesAsync(
            tiposDocumentoContext, titularPorCentro.Values.Distinct().ToList(),
            filas.Select(f => f.TipoDocumentoId).Distinct().ToList(), cancellationToken);

        return filas
            .Where(f => titularPorCentro.ContainsKey(f.CentroId))
            .Select(f => new CondicionDeRequisitoDelCentro(
                f.CentroId, f.TipoDocumentoId, f.Incluido,
                f.Bloquea && ReglaBloqueoDeAcceso.AmbitoPuedeBloquear(f.AmbitoAplicacion),
                // Como VigenciaEnCentro.Condiciones: la fila del Centro solo cuenta si el Tipo está incluido.
                new CondicionesDeAccesoDelCentro(
                    f.Incluido ? f.PeriodicidadEspecialMeses : null,
                    ReglaBloqueoDeAcceso.ResolverToleranciaDias(
                        f.Incluido ? f.ToleranciaDias : null,
                        toleranciasDeCliente.TryGetValue((titularPorCentro[f.CentroId], f.TipoDocumentoId), out var delCliente) ? delCliente : null))))
            .ToList();
    }

    /// <summary>El Cliente empresarial titular de cada Centro (el único sitio de este servicio que lo lee).</summary>
    private async Task<Dictionary<Guid, Guid>> TitularesDeCentrosAsync(IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken) =>
        await centrosContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .Select(c => new { c.Id, Titular = c.ClienteId })
            .ToDictionaryAsync(c => c.Id, c => c.Titular, cancellationToken);

    private async Task<EvaluacionDeAccesoPorCentro> EvaluarEnAlcanceAsync(
        IReadOnlyCollection<Guid>? centroIds, IReadOnlyList<Guid>? centroIdsVisibles, CancellationToken cancellationToken)
    {
        var hoy = DiaDeNegocio.Hoy();

        bool EnAlcance(Guid centroId) =>
            (centroIdsVisibles is null || centroIdsVisibles.Contains(centroId))
            && (centroIds is null || centroIds.Contains(centroId));

        var filas = (await (
            from tc in tiposDocumentoContext.TiposDocumentoCentros
            where tc.Incluido && tc.BloqueaAcceso
            join tipo in tiposDocumentoContext.TiposDocumento on tc.TipoDocumentoId equals tipo.Id
            // Un tipo archivado no bloquea a nadie: no admite altas nuevas (catálogo del 2026-10-17).
            where !tipo.Archivado
            select new { tc.TipoDocumentoId, tc.CentroId, tc.PeriodicidadEspecialMeses, tc.ToleranciaDias, tipo.AmbitoAplicacion })
            .ToListAsync(cancellationToken))
            .Where(f => ReglaBloqueoDeAcceso.AmbitoPuedeBloquear(f.AmbitoAplicacion) && EnAlcance(f.CentroId))
            .ToList();

        if (filas.Count == 0)
            return EvaluacionDeAccesoPorCentro.Vacia;

        var centrosDeFilas = filas.Select(f => f.CentroId).Distinct().ToList();
        var titularPorCentro = await TitularesDeCentrosAsync(centrosDeFilas, cancellationToken);
        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(centrosContext, centrosDeFilas, cancellationToken);
        var clientePorCentro = titularPorCentro
            .Where(c => !sinGestionCae.Contains(c.Key))
            .ToDictionary(c => c.Key, c => c.Value);
        filas = filas.Where(f => clientePorCentro.ContainsKey(f.CentroId)).ToList();

        if (filas.Count == 0)
            return EvaluacionDeAccesoPorCentro.Vacia;

        // Tolerancia por defecto del Cliente empresarial titular de cada Centro, para los Tipos de las filas.
        var titulares = clientePorCentro.Values.Distinct().ToList();
        var tipoIdsDeFilas = filas.Select(f => f.TipoDocumentoId).Distinct().ToList();
        var toleranciasDeCliente = (await tiposDocumentoContext.ToleranciasDocumentoClienteEmpresarial
            .Where(t => titulares.Contains(t.ClienteEmpresarialId) && tipoIdsDeFilas.Contains(t.TipoDocumentoId))
            .Select(t => new { t.ClienteEmpresarialId, t.TipoDocumentoId, t.ToleranciaDias })
            .ToListAsync(cancellationToken))
            .ToDictionary(t => (t.ClienteEmpresarialId, t.TipoDocumentoId), t => t.ToleranciaDias);

        var requisitos = filas
            .Select(f => new RequisitoBloqueanteDelCentro(
                f.CentroId, f.TipoDocumentoId, f.AmbitoAplicacion,
                new CondicionesDeAccesoDelCentro(
                    f.PeriodicidadEspecialMeses,
                    ReglaBloqueoDeAcceso.ResolverToleranciaDias(
                        f.ToleranciaDias,
                        toleranciasDeCliente.TryGetValue((clientePorCentro[f.CentroId], f.TipoDocumentoId), out var delCliente) ? delCliente : null))))
            .ToList();

        var centrosConRequisitos = requisitos.Select(r => r.CentroId).Distinct().ToList();

        // Pendiente en la plataforma (decisión 2026-10-10): solo se mira en los Centros con requisitos bloqueantes, porque
        // solo bloquea un tipo que el Centro marca con BloqueaAcceso; el cruce con cada requisito lo hace
        // CalculoBloqueoDeAccesoDeTrabajadores. Un Centro sin plataforma no devuelve ninguno.
        var pendientes = (await PendientesEnPlataformaDeCentros.CargarAsync(
                centrosContext, documentosContext, tiposDocumentoContext, trabajadoresContext, asignacionesContext.Asignaciones,
                centrosConRequisitos, hoy, cancellationToken))
            .Select(p => new PendienteParaBloqueo(p.CentroId, p.TrabajadorId, p.EmpresaId, p.TipoDocumentoId, p.EstadoAcreditacion))
            .ToList();

        var asignaciones = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null && centrosConRequisitos.Contains(asignacion.CentroId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            select new
            {
                asignacion.CentroId,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                trabajador.EmpresaId,
                trabajador.SubcontrataId
            })
            .ToListAsync(cancellationToken);

        if (asignaciones.Count == 0)
            return EvaluacionDeAccesoPorCentro.Vacia;

        var asignacionesEvaluadas = asignaciones
            .Select(a => new AsignacionEvaluada(a.CentroId, a.TrabajadorId, a.TrabajadorNombre, a.EmpresaId, a.EmpresaId ?? a.SubcontrataId))
            .ToList();

        var trabajadorIds = asignaciones.Select(a => a.TrabajadorId).Distinct().ToList();
        var empresaIds = asignacionesEvaluadas
            .Where(a => a.EmpresaDelTrabajadorId is not null)
            .Select(a => a.EmpresaDelTrabajadorId!.Value)
            .Distinct()
            .ToList();
        var tiposDeTrabajadorIds = requisitos.Where(r => r.Ambito == AmbitoAplicacion.Trabajador).Select(r => r.TipoDocumentoId).Distinct().ToList();
        var tiposDeEmpresaIds = requisitos.Where(r => r.Ambito == AmbitoAplicacion.Empresa).Select(r => r.TipoDocumentoId).Distinct().ToList();

        // La vigencia no se decide en SQL: se traen estado, fechas y emisión y la regla única los evalúa en memoria.
        // Una renuncia nunca vale para acceder (ni hoy ni con tolerancia): no se trae, y el requisito queda como sin documento.
        var documentos = new List<DocumentoParaBloqueo>();

        if (tiposDeTrabajadorIds.Count > 0)
        {
            var delTrabajador = await documentosContext.Documentos.Operativos()
                .Where(d => d.TrabajadorId != null
                    && trabajadorIds.Contains(d.TrabajadorId!.Value)
                    && tiposDeTrabajadorIds.Contains(d.TipoDocumentoId)
                    && !d.EsRenuncia)
                .Select(d => new { d.TrabajadorId, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision })
                .ToListAsync(cancellationToken);
            documentos.AddRange(delTrabajador.Select(d => new DocumentoParaBloqueo(
                d.TrabajadorId, null, d.TipoDocumentoId,
                new DocumentoParaAcceso(VigenciaDocumento.Rehidratar(d.EstadoVigencia, d.FechaVencimiento), d.FechaEmision))));
        }

        if (tiposDeEmpresaIds.Count > 0 && empresaIds.Count > 0)
        {
            var delaEmpresa = await documentosContext.Documentos.Operativos()
                .Where(d => d.EmpresaId != null
                    && empresaIds.Contains(d.EmpresaId!.Value)
                    && tiposDeEmpresaIds.Contains(d.TipoDocumentoId)
                    && !d.EsRenuncia)
                .Select(d => new { d.EmpresaId, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision })
                .ToListAsync(cancellationToken);
            documentos.AddRange(delaEmpresa.Select(d => new DocumentoParaBloqueo(
                null, d.EmpresaId, d.TipoDocumentoId,
                new DocumentoParaAcceso(VigenciaDocumento.Rehidratar(d.EstadoVigencia, d.FechaVencimiento), d.FechaEmision))));
        }

        var evaluados = CalculoBloqueoDeAccesoDeTrabajadores.Evaluar(
            asignacionesEvaluadas
                .Select(a => new AsignacionParaBloqueo(a.CentroId, a.TrabajadorId, a.EmpresaDelTrabajadorId))
                .ToList(),
            requisitos,
            documentos,
            hoy,
            pendientes);

        return new EvaluacionDeAccesoPorCentro(asignacionesEvaluadas, evaluados);
    }
}
