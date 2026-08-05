using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.RequisitosDocumentales;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros;

/// <summary>
/// Causa concreta que empuja el EstadoCentro por debajo de Vigente — un
/// Documento (de la Empresa o de un Trabajador) que no está Vigente, o un
/// RequisitoDocumental bloqueante sin Cumplido. Solo se generan causas para
/// lo que efectivamente aporta al peor caso — nada Vigente aparece aquí,
/// igual que ObtenerAlertasQuery no lista Documentos al día.
/// </summary>
public record CausaEstadoCentro(string Descripcion, EstadoDocumento? Estado, bool Bloqueante);

public record ResultadoEstadoCentro(EstadoCentro Estado, IReadOnlyList<CausaEstadoCentro> Causas);

/// <summary>
/// Cálculo compartido entre ObtenerCentrosQuery (badge de la tabla) y
/// ObtenerEstadoCentroQuery (desglose del Workspace) — agrega de una sola
/// vez los Documentos de Empresa, los Documentos y huecos obligatorios de
/// cada Trabajador con Asignación activa, y los RequisitosDocumentales
/// bloqueantes de uno o varios Centros, para no lanzar N consultas al pintar
/// una página de la tabla. La lógica de "documento faltante" replica la de
/// ObtenerAlertasQuery.ObtenerFaltantesAsync (Trabajador únicamente — los
/// Documentos de Empresa aquí solo aportan su vigencia, sin detección de
/// falta total, mismo alcance que esa Query).
/// </summary>
public interface ICalculoEstadoCentroService
{
    Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken);
}

public class CalculoEstadoCentroService(
    ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    IRequisitosDocumentalesQueryContext requisitosContext,
    IConfiguracionQueryContext configuracionContext)
    : ICalculoEstadoCentroService
{
    public async Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return new Dictionary<Guid, ResultadoEstadoCentro>();

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var causasPorCentro = centroIds.Distinct().ToDictionary(id => id, _ => new List<CausaEstadoCentro>());

        await AgregarCausasDeEmpresaAsync(centroIds, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, causasPorCentro, cancellationToken);
        await AgregarCausasDeTrabajadorAsync(centroIds, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, causasPorCentro, cancellationToken);
        await AgregarCausasDeRequisitosBloqueantesAsync(centroIds, causasPorCentro, cancellationToken);

        return causasPorCentro.ToDictionary(
            par => par.Key,
            par => new ResultadoEstadoCentro(
                CalculadoraEstadoCentro.Calcular(
                    par.Value.Where(c => c.Estado is not null).Select(c => c.Estado!.Value).ToList(),
                    par.Value.Any(c => c.Bloqueante)),
                par.Value));
    }

    private async Task AgregarCausasDeEmpresaAsync(
        IReadOnlyList<Guid> centroIds, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias,
        Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        var centros = await centrosContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .Select(c => new { c.Id, c.EmpresaId })
            .ToListAsync(cancellationToken);

        var centroIdsPorEmpresa = centros
            .GroupBy(c => c.EmpresaId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());

        var empresaIds = centroIdsPorEmpresa.Keys.ToList();

        var documentosEmpresa = await (
            from documento in documentosContext.Documentos
            where documento.EmpresaId != null && empresaIds.Contains(documento.EmpresaId!.Value)
            where documento.FechaVencimiento != null
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new { EmpresaId = documento.EmpresaId!.Value, documento.FechaVencimiento, tipoDocumento.Nombre })
            .ToListAsync(cancellationToken);

        foreach (var documento in documentosEmpresa)
        {
            var estado = CalculadoraEstadoDocumento.Calcular(documento.FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias);
            if (estado is EstadoDocumento.NoAplica or EstadoDocumento.Vigente) continue;
            if (!centroIdsPorEmpresa.TryGetValue(documento.EmpresaId, out var centrosDeEmpresa)) continue;

            var causa = new CausaEstadoCentro($"{documento.Nombre} — Empresa", estado, Bloqueante: false);
            foreach (var centroId in centrosDeEmpresa)
                causasPorCentro[centroId].Add(causa);
        }
    }

    private async Task AgregarCausasDeTrabajadorAsync(
        IReadOnlyList<Guid> centroIds, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias,
        Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        var asignacionesActivas = await (
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

        if (asignacionesActivas.Count == 0) return;

        var trabajadorIds = asignacionesActivas.Select(a => a.TrabajadorId).Distinct().ToList();

        // Vigencia de los Documentos de Trabajador que ya existen — igual que
        // el bloque "alertasVigencia" de ObtenerAlertasQuery, sin filtrar por
        // EsObligatorio: un Documento vencido cuenta para el Centro exista o
        // no exista una fila de obligatoriedad para su TipoDocumento.
        var documentosTrabajador = await (
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null && trabajadorIds.Contains(documento.TrabajadorId!.Value)
            where documento.FechaVencimiento != null
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new { TrabajadorId = documento.TrabajadorId!.Value, documento.FechaVencimiento, tipoDocumento.Nombre })
            .ToListAsync(cancellationToken);

        foreach (var asignacion in asignacionesActivas)
        {
            foreach (var documento in documentosTrabajador.Where(d => d.TrabajadorId == asignacion.TrabajadorId))
            {
                var estado = CalculadoraEstadoDocumento.Calcular(documento.FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias);
                if (estado is EstadoDocumento.NoAplica or EstadoDocumento.Vigente) continue;

                causasPorCentro[asignacion.CentroId].Add(
                    new CausaEstadoCentro($"{documento.Nombre} — {asignacion.TrabajadorNombre}", estado, Bloqueante: false));
            }
        }

        // Huecos obligatorios — misma lógica que
        // ObtenerAlertasQuery.ObtenerFaltantesAsync, reacotada a estos Centros.
        var tiposObligatorios = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador && t.EsObligatorio)
            .Select(t => new { t.Id, t.Nombre })
            .ToListAsync(cancellationToken);

        if (tiposObligatorios.Count == 0) return;

        var tipoIdsObligatorios = tiposObligatorios.Select(t => t.Id).ToHashSet();

        var restriccionesPorTipo = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsObligatorios.Contains(tc.TipoDocumentoId))
            .Select(tc => new { tc.TipoDocumentoId, tc.CentroId })
            .ToListAsync(cancellationToken))
            .GroupBy(tc => tc.TipoDocumentoId)
            .ToDictionary(g => g.Key, g => g.Select(tc => tc.CentroId).ToHashSet());

        var parejasConDocumento = (await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsObligatorios.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId })
            .ToListAsync(cancellationToken))
            .Select(d => (d.TrabajadorId, d.TipoDocumentoId))
            .ToHashSet();

        foreach (var asignacion in asignacionesActivas)
        {
            foreach (var tipo in tiposObligatorios)
            {
                if (restriccionesPorTipo.TryGetValue(tipo.Id, out var centrosPermitidos)
                    && !centrosPermitidos.Contains(asignacion.CentroId))
                    continue;

                if (parejasConDocumento.Contains((asignacion.TrabajadorId, tipo.Id)))
                    continue;

                causasPorCentro[asignacion.CentroId].Add(new CausaEstadoCentro(
                    $"{tipo.Nombre} — {asignacion.TrabajadorNombre}", EstadoDocumento.Faltante, Bloqueante: false));
            }
        }
    }

    private async Task AgregarCausasDeRequisitosBloqueantesAsync(
        IReadOnlyList<Guid> centroIds, Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        var requisitosBloqueantes = await requisitosContext.RequisitosDocumentales
            .Where(r => centroIds.Contains(r.CentroId) && r.BloqueaAcceso && !r.Cumplido)
            .Select(r => new { r.CentroId, r.Descripcion })
            .ToListAsync(cancellationToken);

        foreach (var requisito in requisitosBloqueantes)
            causasPorCentro[requisito.CentroId].Add(new CausaEstadoCentro(requisito.Descripcion, Estado: null, Bloqueante: true));
    }
}
