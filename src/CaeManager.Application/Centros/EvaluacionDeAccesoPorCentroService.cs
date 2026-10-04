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
/// y qué Asignaciones se consideran. Lo usan Mi trabajo (vía <c>ObtenerDocumentacionBloqueantePendienteQuery</c>), el detalle
/// por Trabajador del Centro 360, los KPI de Inicio y de la Visión de cartera, y la marca «bloquea acceso» de la cola, para que
/// ninguna superficie recalcule el bloqueo.
///
/// <para>
/// Dos fuentes, un solo resultado (<see cref="RequisitoEvaluado"/>): los Tipos que el Centro marca como bloqueantes
/// (<see cref="SituacionDeRequisitoBloqueante.Ausente"/> y <see cref="SituacionDeRequisitoBloqueante.Vencido"/>, con su tolerancia) y
/// el veredicto de la plataforma del Cliente empresarial (<see cref="SituacionDeRequisitoBloqueante.VencidoEnPlataforma"/> y
/// <see cref="SituacionDeRequisitoBloqueante.RechazadoPorPlataforma"/>, sin tolerancia). Ninguna de las dos marca el Centro entero:
/// bloquean al Trabajador, o a los Trabajadores de la Empresa, afectados en ese Centro (decisión del propietario, 2026-10-04).
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

public class EvaluacionDeAccesoPorCentroService(
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    IDocumentosQueryContext documentosContext,
    IAlcanceDatosService alcanceDatos)
    : IEvaluacionDeAccesoPorCentroService
{
    public async Task<EvaluacionDeAccesoPorCentro> EvaluarAsync(
        IReadOnlyCollection<Guid>? centroIds, CancellationToken cancellationToken)
    {
        var hoy = DiaDeNegocio.Hoy();

        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        bool EnAlcance(Guid centroId) =>
            (centroIdsVisibles is null || centroIdsVisibles.Contains(centroId))
            && (centroIds is null || centroIds.Contains(centroId));

        var requisitos = await CargarRequisitosDeDocumentoAsync(EnAlcance, cancellationToken);
        var veredictosDePlataforma = await CargarVeredictosDePlataformaAsync(centroIds, EnAlcance, hoy, cancellationToken);

        if (requisitos.Count == 0 && veredictosDePlataforma.Count == 0)
            return EvaluacionDeAccesoPorCentro.Vacia;

        var centrosConRequisitos = requisitos.Select(r => r.CentroId)
            .Concat(veredictosDePlataforma.Select(v => v.CentroId))
            .Distinct()
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
        var documentos = new List<DocumentoParaBloqueo>();

        if (tiposDeTrabajadorIds.Count > 0)
        {
            var delTrabajador = await documentosContext.Documentos.Operativos()
                .Where(d => d.TrabajadorId != null
                    && trabajadorIds.Contains(d.TrabajadorId!.Value)
                    && tiposDeTrabajadorIds.Contains(d.TipoDocumentoId))
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
                    && tiposDeEmpresaIds.Contains(d.TipoDocumentoId))
                .Select(d => new { d.EmpresaId, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision })
                .ToListAsync(cancellationToken);
            documentos.AddRange(delaEmpresa.Select(d => new DocumentoParaBloqueo(
                null, d.EmpresaId, d.TipoDocumentoId,
                new DocumentoParaAcceso(VigenciaDocumento.Rehidratar(d.EstadoVigencia, d.FechaVencimiento), d.FechaEmision))));
        }

        var asignacionesParaBloqueo = asignacionesEvaluadas
            .Select(a => new AsignacionParaBloqueo(a.CentroId, a.TrabajadorId, a.EmpresaDelTrabajadorId))
            .ToList();

        var evaluados = CalculoBloqueoDeAccesoDeTrabajadores.Evaluar(asignacionesParaBloqueo, requisitos, documentos, hoy)
            .Concat(CalculoBloqueoDeAccesoDeTrabajadores.EvaluarPlataforma(asignacionesParaBloqueo, veredictosDePlataforma))
            .ToList();

        return new EvaluacionDeAccesoPorCentro(asignacionesEvaluadas, evaluados);
    }

    /// <summary>
    /// Los Tipos que cada Centro del alcance marca como bloqueantes (<c>BloqueaAcceso</c>), con su vigencia propia y la tolerancia ya
    /// resuelta (la del Centro si la personaliza; si no, la de su Cliente empresarial titular; si no, 0). Un Centro sin gestión CAE
    /// no exige nada: su marca no declara requisito (P1-X2).
    /// </summary>
    private async Task<List<RequisitoBloqueanteDelCentro>> CargarRequisitosDeDocumentoAsync(
        Func<Guid, bool> enAlcance, CancellationToken cancellationToken)
    {
        var filas = (await (
            from tc in tiposDocumentoContext.TiposDocumentoCentros
            where tc.Incluido && tc.BloqueaAcceso
            join tipo in tiposDocumentoContext.TiposDocumento on tc.TipoDocumentoId equals tipo.Id
            select new { tc.TipoDocumentoId, tc.CentroId, tc.PeriodicidadEspecialMeses, tc.ToleranciaDias, tipo.AmbitoAplicacion })
            .ToListAsync(cancellationToken))
            .Where(f => ReglaBloqueoDeAcceso.AmbitoPuedeBloquear(f.AmbitoAplicacion) && enAlcance(f.CentroId))
            .ToList();

        if (filas.Count == 0)
            return [];

        var centrosDeFilas = filas.Select(f => f.CentroId).Distinct().ToList();
        var centros = await centrosContext.Centros
            .Where(c => centrosDeFilas.Contains(c.Id))
            .Select(c => new { c.Id, Titular = c.ClienteId })
            .ToListAsync(cancellationToken);
        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(centrosContext, centrosDeFilas, cancellationToken);
        var clientePorCentro = centros
            .Where(c => !sinGestionCae.Contains(c.Id))
            .ToDictionary(c => c.Id, c => c.Titular);
        filas = filas.Where(f => clientePorCentro.ContainsKey(f.CentroId)).ToList();

        if (filas.Count == 0)
            return [];

        // Tolerancia por defecto del Cliente empresarial titular de cada Centro, para los Tipos de las filas.
        var titulares = clientePorCentro.Values.Distinct().ToList();
        var tipoIdsDeFilas = filas.Select(f => f.TipoDocumentoId).Distinct().ToList();
        var toleranciasDeCliente = (await tiposDocumentoContext.ToleranciasDocumentoClienteEmpresarial
            .Where(t => titulares.Contains(t.ClienteEmpresarialId) && tipoIdsDeFilas.Contains(t.TipoDocumentoId))
            .Select(t => new { t.ClienteEmpresarialId, t.TipoDocumentoId, t.ToleranciaDias })
            .ToListAsync(cancellationToken))
            .ToDictionary(t => (t.ClienteEmpresarialId, t.TipoDocumentoId), t => t.ToleranciaDias);

        return filas
            .Select(f => new RequisitoBloqueanteDelCentro(
                f.CentroId, f.TipoDocumentoId, f.AmbitoAplicacion,
                new CondicionesDeAccesoDelCentro(
                    f.PeriodicidadEspecialMeses,
                    ReglaBloqueoDeAcceso.ResolverToleranciaDias(
                        f.ToleranciaDias,
                        toleranciasDeCliente.TryGetValue((clientePorCentro[f.CentroId], f.TipoDocumentoId), out var delCliente) ? delCliente : null))))
            .ToList();
    }

    /// <summary>
    /// El veredicto de la plataforma del Cliente empresarial en los Centros del alcance (<see cref="AcreditacionesEnPlataformaDeCentros"/>):
    /// acreditaciones vencidas allí o rechazadas y aplicables. No depende de que el Centro marque ningún Tipo como bloqueante: lo
    /// decide la plataforma. Un Centro sin gestión CAE no exige nada, tampoco por la plataforma que conserve de antes (P1-X2).
    /// </summary>
    private async Task<List<AcreditacionEnPlataformaDeCentro>> CargarVeredictosDePlataformaAsync(
        IReadOnlyCollection<Guid>? centroIds, Func<Guid, bool> enAlcance, DateOnly hoy, CancellationToken cancellationToken)
    {
        var limitarA = centroIds?.ToList();
        var veredictos = (await AcreditacionesEnPlataformaDeCentros.LeerVencidasAsync(
                documentosContext, centrosContext, tiposDocumentoContext, limitarA, hoy, cancellationToken))
            .Concat(await AcreditacionesEnPlataformaDeCentros.LeerRechazadasAplicablesAsync(
                documentosContext, centrosContext, tiposDocumentoContext, limitarA, cancellationToken))
            .Where(v => enAlcance(v.CentroId))
            .ToList();

        if (veredictos.Count == 0)
            return [];

        var centrosDeVeredictos = veredictos.Select(v => v.CentroId).Distinct().ToList();
        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(centrosContext, centrosDeVeredictos, cancellationToken);
        return veredictos.Where(v => !sinGestionCae.Contains(v.CentroId)).ToList();
    }
}
