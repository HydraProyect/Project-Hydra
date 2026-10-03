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

        var filas = (await (
            from tc in tiposDocumentoContext.TiposDocumentoCentros
            where tc.Incluido && tc.BloqueaAcceso
            join tipo in tiposDocumentoContext.TiposDocumento on tc.TipoDocumentoId equals tipo.Id
            select new { tc.TipoDocumentoId, tc.CentroId, tc.PeriodicidadEspecialMeses, tc.ToleranciaDias, tipo.AmbitoAplicacion })
            .ToListAsync(cancellationToken))
            .Where(f => ReglaBloqueoDeAcceso.AmbitoPuedeBloquear(f.AmbitoAplicacion) && EnAlcance(f.CentroId))
            .ToList();

        if (filas.Count == 0)
            return EvaluacionDeAccesoPorCentro.Vacia;

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

        var evaluados = CalculoBloqueoDeAccesoDeTrabajadores.Evaluar(
            asignacionesEvaluadas
                .Select(a => new AsignacionParaBloqueo(a.CentroId, a.TrabajadorId, a.EmpresaDelTrabajadorId))
                .ToList(),
            requisitos,
            documentos,
            hoy);

        return new EvaluacionDeAccesoPorCentro(asignacionesEvaluadas, evaluados);
    }
}
