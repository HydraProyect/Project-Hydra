using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Visitas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Antelacion;

public interface IEvaluadorExpedienteVisitaService
{
    /// <summary>Evalúa una Visita concreta y sella su expediente si ya está completo. Devuelve true si esta llamada fue la que selló.</summary>
    Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reevalúa las visitas todavía sin sellar a las que afecta un Documento — las del
    /// trabajador si es documentación de trabajador, o las de los centros de la empresa
    /// si es documentación de empresa. Es el disparador natural al crear o renovar un
    /// documento; los documentos de otros ámbitos (cliente, vehículo, proyecto) no
    /// intervienen en el expediente de una visita y se ignoran.
    /// </summary>
    Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Decide cuándo el expediente documental de una Visita pasa a estar completo y
/// congela ahí la matriz de antelación (<see cref="CalculadoraAntelacionVisita"/>).
///
/// "Completo" significa que ningún documento <b>requerido</b> del Centro está
/// <see cref="EstadoDocumento.Faltante"/> ni <see cref="EstadoDocumento.Vencido"/>,
/// ni para la Empresa titular ni para ninguno de los Trabajadores de la visita.
/// Se incluye Vencido a propósito: un apto médico caducado bloquea la entrada
/// igual que uno que no existe, y llamar "completo" a ese expediente falsearía a
/// favor del cliente el margen que tuvo el gestor.
///
/// El sello es de un solo sentido: si después se añade un trabajador y vuelve a
/// faltar papel, no se retira (ver <see cref="Visita.MarcarExpedienteCompleto"/>).
/// </summary>
public class EvaluadorExpedienteVisitaService(
    IVisitasQueryContext visitasContext,
    ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IConfiguracionQueryContext configuracionContext,
    IVisitaRepository visitaRepository,
    IUnitOfWork unitOfWork,
    ILogger<EvaluadorExpedienteVisitaService> logger)
    : IEvaluadorExpedienteVisitaService
{
    public async Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default)
    {
        var visita = await visitaRepository.ObtenerPorIdAsync(visitaId, cancellationToken);

        // Sin solicitud de origen no hay nada que medir (visita creada a mano), y con el
        // expediente ya sellado no hay nada que rehacer. Se sale antes de consultar nada.
        if (visita is null || visita.FechaHoraSolicitudUtc is null || visita.FechaHoraExpedienteCompletoUtc is not null)
            return false;

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        if (!await ExpedienteCompletoAsync(visita, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, cancellationToken))
            return false;

        var ahora = DateTime.UtcNow;
        var antelacion = CalculadoraAntelacionVisita.Calcular(
            visita.ObtenerFechaHoraEntrada(parametros.HoraInicioJornada),
            visita.FechaHoraSolicitudUtc.Value,
            ahora,
            parametros.HorasAvisoVisita,
            parametros.HorasCriticasVisita,
            parametros.HoraInicioJornada,
            parametros.HoraFinJornada);

        if (!visita.MarcarExpedienteCompleto(ahora, antelacion)) return false;

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Expediente de la visita {VisitaId} completo: antelación nominal {Nominal}h, efectiva {Efectiva}h, tramo {Tramo}, atribución {Atribucion}.",
            visita.Id, antelacion.NominalHoras, antelacion.EfectivaHoras, antelacion.Tramo, antelacion.Atribucion);

        return true;
    }

    public async Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default)
    {
        var propietario = await documentosContext.Documentos
            .Where(d => d.Id == documentoId)
            .Select(d => new { d.TrabajadorId, d.EmpresaId })
            .FirstOrDefaultAsync(cancellationToken);

        if (propietario is null) return;

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        // Solo las que todavía tienen algo que sellar: con solicitud de origen, sin sello
        // previo y sin haber pasado ya. Una visita antigua no gana nada por reevaluarse.
        var pendientes = visitasContext.Visitas
            .Where(v => v.FechaHoraSolicitudUtc != null && v.FechaHoraExpedienteCompletoUtc == null && v.FechaFin >= hoy);

        List<Guid> visitaIds;

        if (propietario.TrabajadorId is { } trabajadorId)
        {
            visitaIds = await visitasContext.VisitasTrabajadores
                .Where(vt => vt.TrabajadorId == trabajadorId)
                .Join(pendientes, vt => vt.VisitaId, v => v.Id, (vt, v) => v.Id)
                .Distinct()
                .ToListAsync(cancellationToken);
        }
        else if (propietario.EmpresaId is { } empresaId)
        {
            var centroIds = await centrosContext.Centros
                .Where(c => c.EmpresaId == empresaId)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            visitaIds = await pendientes
                .Where(v => centroIds.Contains(v.CentroId))
                .Select(v => v.Id)
                .ToListAsync(cancellationToken);
        }
        else
        {
            return;
        }

        foreach (var visitaId in visitaIds)
            await EvaluarAsync(visitaId, cancellationToken);
    }

    private async Task<bool> ExpedienteCompletoAsync(
        Visita visita, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias, CancellationToken cancellationToken)
    {
        var empresaId = await centrosContext.Centros
            .Where(c => c.Id == visita.CentroId)
            .Select(c => (Guid?)c.EmpresaId)
            .FirstOrDefaultAsync(cancellationToken);

        if (empresaId is null) return false;

        var tipos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa || t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.AmbitoAplicacion, t.EsObligatorio })
            .ToListAsync(cancellationToken);

        var filasDelCentro = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tc.CentroId == visita.CentroId)
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var tiposRequeridosEmpresa = tipos
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa
                     && ResolucionTipoDocumentoCentro.Aplica(filasDelCentro, t.Id, visita.CentroId, t.EsObligatorio))
            .Select(t => t.Id)
            .ToList();

        var tiposRequeridosTrabajador = tipos
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador
                     && ResolucionTipoDocumentoCentro.Aplica(filasDelCentro, t.Id, visita.CentroId, t.EsObligatorio))
            .Select(t => t.Id)
            .ToList();

        var documentosEmpresa = await documentosContext.Documentos
            .Where(d => d.EmpresaId == empresaId)
            .Select(d => new { d.TipoDocumentoId, d.FechaVencimiento })
            .ToListAsync(cancellationToken);

        if (!TiposCubiertos(tiposRequeridosEmpresa, documentosEmpresa.Select(d => (d.TipoDocumentoId, d.FechaVencimiento)), hoy, umbralAmbarDias, umbralRojoDias))
            return false;

        var trabajadorIds = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == visita.Id)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        // Una visita sin trabajadores todavía no es un expediente que se pueda dar por
        // completo — es una visita a medio dar de alta.
        if (trabajadorIds.Count == 0) return false;

        var documentosTrabajadores = await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId!.Value))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.FechaVencimiento })
            .ToListAsync(cancellationToken);

        foreach (var trabajadorId in trabajadorIds)
        {
            var suyos = documentosTrabajadores
                .Where(d => d.TrabajadorId == trabajadorId)
                .Select(d => (d.TipoDocumentoId, d.FechaVencimiento));

            if (!TiposCubiertos(tiposRequeridosTrabajador, suyos, hoy, umbralAmbarDias, umbralRojoDias))
                return false;
        }

        return true;
    }

    private static bool TiposCubiertos(
        IReadOnlyCollection<Guid> tiposRequeridos,
        IEnumerable<(Guid TipoDocumentoId, DateOnly? FechaVencimiento)> documentos,
        DateOnly hoy, int umbralAmbarDias, int umbralRojoDias)
    {
        if (tiposRequeridos.Count == 0) return true;

        var tiposVigentes = documentos
            .Where(d => CalculadoraEstadoDocumento.Calcular(d.FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias) != EstadoDocumento.Vencido)
            .Select(d => d.TipoDocumentoId)
            .ToHashSet();

        return tiposRequeridos.All(tiposVigentes.Contains);
    }
}
