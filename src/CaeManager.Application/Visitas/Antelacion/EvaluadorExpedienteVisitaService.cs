using CaeManager.Domain.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
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
/// "Completo" significa que <b>ningún participante de la Visita está bloqueado en su Centro</b>, con la misma regla
/// de bloqueo que el resto de superficies (D-4, decisión del 2026-11-15 sobre la #2005): solo cuentan los requisitos
/// que el Centro marca con <c>BloqueaAcceso</c>, con la vigencia propia y la tolerancia del Centro, y un documento
/// de Empresa ausente o que ya no vale bloquea a todos los participantes de esa Empresa
/// (<see cref="ReglaBloqueoDeAcceso"/>, <see cref="IEvaluacionDeAccesoDelTenant.EvaluarTrabajadoresEnCentroAsync"/>).
/// Hasta esa decisión el expediente tenía una regla propia —todo documento requerido, sin tolerancia y con los
/// documentos de la Empresa del Centro— que podía dar por incompleto a quien el Centro deja entrar, o al revés.
///
/// El sello es de un solo sentido: si después se añade un trabajador y vuelve a
/// faltar papel, no se retira (ver <see cref="Visita.MarcarExpedienteCompleto"/>).
/// La vía de revisión y reapertura por el Gestor CAE que pide D-4 está pendiente de
/// una decisión de producto (estado al que vuelve, quién puede reabrir y qué se audita).
/// </summary>
public class EvaluadorExpedienteVisitaService(
    IVisitasQueryContext visitasContext,
    ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext,
    IEvaluacionDeAccesoDelTenant evaluacionDeAcceso,
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
        // Una cancelada tampoco (FS-11): no está vigente; al reactivarla se evalúa de nuevo.
        if (visita is null || visita.EstaCancelada || visita.FechaHoraSolicitudUtc is null || visita.FechaHoraExpedienteCompletoUtc is not null)
            return false;

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);

        if (!await ExpedienteCompletoAsync(visita, cancellationToken))
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

        var hoy = DiaDeNegocio.Hoy();

        // Solo las que todavía tienen algo que sellar: con solicitud de origen, sin sello
        // previo y sin haber pasado ya. Una visita antigua no gana nada por reevaluarse.
        var pendientes = visitasContext.Visitas
            .Where(v => v.FechaHoraSolicitudUtc != null && v.FechaHoraExpedienteCompletoUtc == null && !v.EstaCancelada && v.FechaFin >= hoy);

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

    private async Task<bool> ExpedienteCompletoAsync(Visita visita, CancellationToken cancellationToken)
    {
        var centro = await centrosContext.Centros
            .Where(c => c.Id == visita.CentroId)
            .Select(c => new { c.GestionCae })
            .FirstOrDefaultAsync(cancellationToken);

        if (centro is null) return false;

        // P1-X2: un Centro sin gestión CAE no exige documentos — el expediente
        // no tiene nada que reunir y está completo en cuanto sabe quién va. Una
        // visita sin participantes sigue incompleta, igual que con gestión CAE:
        // el sello es irreversible y no se recalcula al añadir Trabajadores.
        if (centro.GestionCae == Domain.Centros.ModalidadGestionCae.SinGestionCae)
            return await visitasContext.VisitasTrabajadores.AnyAsync(vt => vt.VisitaId == visita.Id, cancellationToken);

        var trabajadorIds = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == visita.Id)
            .Select(vt => vt.TrabajadorId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Una visita sin trabajadores todavía no es un expediente que se pueda dar por
        // completo — es una visita a medio dar de alta.
        if (trabajadorIds.Count == 0) return false;

        // D-4 (#2005, decisión del 2026-11-15): la misma regla de bloqueo que el resto de superficies, sin copia propia.
        // Completo = ningún requisito bloqueante del Centro (BloqueaAcceso, con su vigencia propia y su tolerancia)
        // bloquea a ningún participante, ni por un documento suyo ni por uno de su Empresa.
        var requisitos = await evaluacionDeAcceso.EvaluarTrabajadoresEnCentroAsync(visita.CentroId, trabajadorIds, cancellationToken);
        return !requisitos.Any(r => ReglaBloqueoDeAcceso.Bloquea(r.Resultado.Situacion));
    }
}
