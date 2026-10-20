using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Application.Visitas.Eventos;
using CaeManager.Application.Visitas.PaqueteDocumental;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Commands.CrearVisita;

/// <summary>
/// <paramref name="SugerenciaVisitaCorreoId"/> (opcional): cuando la visita
/// se crea desde el botón "Crear visita" de una sugerencia detectada por IA
/// en un correo (ver SugerenciaVisitaCorreo), viene con el Id de esa
/// sugerencia para marcarla resuelta en la misma operación — así no queda
/// pendiente en la Bandeja una sugerencia que ya se atendió.
/// </summary>
public record CrearVisitaCommand(
    Guid CentroId, DateOnly FechaInicio, DateOnly FechaFin, IReadOnlyList<Guid> TrabajadorIds, string? Notas,
    Guid? SugerenciaVisitaCorreoId = null, TimeOnly? HoraEstimadaAcceso = null)
    : ICommand<Guid>;

public class CrearVisitaCommandValidator : AbstractValidator<CrearVisitaCommand>
{
    public CrearVisitaCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("La visita debe tener un centro.");

        RuleFor(c => c.FechaFin)
            .GreaterThanOrEqualTo(c => c.FechaInicio)
            .WithMessage("La fecha de fin no puede ser anterior a la fecha de inicio.");

        RuleFor(c => c.TrabajadorIds)
            .NotEmpty().WithMessage("La visita debe incluir al menos un trabajador.");

        RuleFor(c => c.Notas)
            .MaximumLength(Visita.LongitudMaximaNotas)
            .WithMessage($"Las notas no pueden superar {Visita.LongitudMaximaNotas} caracteres.");
    }
}

public class CrearVisitaCommandHandler(
    IVisitaRepository repositorio, IVisitaTrabajadorRepository visitaTrabajadorRepositorio,
    ICentrosQueryContext centrosContext, ITrabajadoresQueryContext trabajadoresContext,
    ISugerenciaVisitaCorreoRepository sugerenciaRepositorio, IComunicacionesQueryContext comunicacionesContext,
    IPaqueteDocumentalVisitaService paqueteDocumental, IEvaluadorExpedienteVisitaService evaluadorExpediente,
    ICurrentUserService currentUserService, IPublisher publisher, IUnitOfWork unitOfWork,
    ILogger<CrearVisitaCommandHandler> logger, IAlcanceDatosService alcanceDatos,
    IActorAuditoria? actorAuditoria = null)
    : IRequestHandler<CrearVisitaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearVisitaCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de Project-Hydra-Negocio/MATURITY_REVIEW.md.
        // El contacto del Centro se lee en la misma consulta: es el valor por defecto del contacto
        // de la Visita (decisión de producto, 2026-10-16), que luego puede cambiarse en Visita 360.
        var centro = await centrosContext.Centros
            .Where(c => c.Id == request.CentroId)
            .Select(c => new { c.Contacto })
            .FirstOrDefaultAsync(cancellationToken);
        if (centro is null)
            return CentroNoEncontrado();

        // Alcance de cartera: existir en el Tenant no basta. Un Gestor CAE solo
        // crea Visitas sobre Centros dentro de su Asignación de Cartera —el mismo
        // criterio con el que las lecturas de Visita (ObtenerVisitaPorId,
        // ObtenerDetalleVisita, ObtenerVisitas) deciden si se ve—; sin esto,
        // conocer el Id de un Centro fuera de cartera bastaba para crearle una
        // Visita que luego ni siquiera veía. Variante «ParaGestion»: crear es
        // operar, no leer. Se responde igual que "no existe" para no revelar qué
        // hay fuera del alcance.
        //
        // Los Trabajadores NO se acotan a la cartera: el formulario los ofrece de
        // la base general del Tenant (AlcanceSelectorTrabajadores.BaseGeneralDelTenant)
        // porque un Trabajador de una Subcontrata puede acudir a Centros de
        // distintos Gestores CAE; acotarlos es una decisión de producto aún no
        // tomada. RLS y el filtro de Tenant siguen aplicando a su existencia.
        if (!await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, cancellationToken))
            return CentroNoEncontrado();

        var trabajadorIds = request.TrabajadorIds.Distinct().ToList();
        var encontrados = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => t.Id)
            .CountAsync(cancellationToken);

        if (encontrados != trabajadorIds.Count)
            return Result.Fallo<Guid>(Error.Crear("Visita.TrabajadorNoEncontrado", "Alguno de los trabajadores seleccionados no existe."));

        // Asignación de Cartera de la sugerencia: solo cuenta si quien mira alcanza la Conversación
        // del mensaje que la originó, el mismo criterio con el que ObtenerConversacionPorIdQuery la
        // enseña. Fuera de alcance se trata igual que un Id que no existe: la Visita nace, pero la
        // sugerencia de otra cartera no se resuelve, su Conversación no se enlaza y no se le envía
        // ningún paquete documental.
        var sugerencia = request.SugerenciaVisitaCorreoId is { } sugerenciaId
            ? await ObtenerSugerenciaDentroDeAlcanceAsync(sugerenciaId, cancellationToken)
            : null;

        // El origen sale del canal del mensaje del que nació la sugerencia, no de que exista una:
        // una sugerencia detectada por WhatsApp crea una Visita de origen WhatsApp.
        CanalConversacion? canalDelMensaje = null;
        if (sugerencia is not null)
        {
            canalDelMensaje = await comunicacionesContext.Mensajes
                .Where(m => m.Id == sugerencia.MensajeId)
                .Select(m => (CanalConversacion?)m.Canal)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var origen = Visita.OrigenAlCrear(sugerencia is not null, canalDelMensaje);
        var visita = new Visita(request.CentroId, request.FechaInicio, request.FechaFin, request.Notas, origen, request.HoraEstimadaAcceso);
        visita.RellenarContactoDesdeCentro(centro.Contacto);
        repositorio.Agregar(visita);

        var gestionadoPor = await GestorDeLaAsignacion.ActorRealAsync(actorAuditoria);
        foreach (var trabajadorId in trabajadorIds)
            visitaTrabajadorRepositorio.Agregar(new VisitaTrabajador(visita.Id, trabajadorId, gestionadoPor));

        Guid? conversacionParaPaquete = null;
        if (sugerencia is not null)
        {
            // Si el Gestor tuvo que corregir centro o fechas antes de confirmar, la
            // sugerencia ahorró menos trabajo que una aceptada de un clic. No hace
            // falta que la UI lo declare: los propios datos lo dicen.
            var huboEdicion =
                sugerencia.CentroId != request.CentroId
                || (sugerencia.FechaInicioSugerida is { } inicio && inicio != request.FechaInicio)
                || (sugerencia.FechaFinSugerida is { } fin && fin != request.FechaFin);

            sugerencia.Resolver(
                huboEdicion ? ResolucionSugerencia.ConfirmadaConEdicion : ResolucionSugerencia.Confirmada,
                await currentUserService.ObtenerUsuarioActualIdAsync());
            conversacionParaPaquete = await comunicacionesContext.Mensajes
                .Where(m => m.Id == sugerencia.MensajeId)
                .Select(m => (Guid?)m.ConversacionId)
                .FirstOrDefaultAsync(cancellationToken);

            // Instante que el cliente considera "yo avisé": el primer mensaje entrante
            // del hilo, no el mensaje del que la IA sacó la sugerencia — si el hilo
            // arrancó pidiendo la visita y luego hubo idas y venidas, el margen se
            // cuenta desde el principio, a favor del cliente.
            if (conversacionParaPaquete is { } conversacionOrigenId)
            {
                var solicitudUtc = await comunicacionesContext.Mensajes
                    .Where(m => m.ConversacionId == conversacionOrigenId && m.Direccion == DireccionMensaje.Entrante)
                    .OrderBy(m => m.FechaUtc)
                    .Select(m => (DateTime?)m.FechaUtc)
                    .FirstOrDefaultAsync(cancellationToken);

                if (solicitudUtc is not null)
                    visita.RegistrarOrigenSolicitud(conversacionOrigenId, solicitudUtc.Value);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Una visita puede nacer ya con todo el papeleo en regla — si es así, el
        // expediente se sella aquí mismo y la antelación efectiva coincide con la nominal.
        try
        {
            await evaluadorExpediente.EvaluarAsync(visita.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo evaluar el expediente documental de la visita {VisitaId}.", visita.Id);
        }

        // Mejor esfuerzo, en una segunda operación separada: un fallo
        // generando el paquete documental (storage inconsistente, hilo
        // borrado entre medias) no debe deshacer la Visita ya creada, que es
        // el resultado principal de este Command.
        if (conversacionParaPaquete is { } conversacionId)
        {
            try
            {
                await paqueteDocumental.GenerarYEnviarAsync(visita.Id, conversacionId, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo generar el paquete documental automático para la visita {VisitaId}.", visita.Id);
            }

            // Publicado DESPUÉS del commit (Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md
            // § 6.5): un evento sobre una Visita que todavía no existe en la
            // base de datos rompería al suscriptor que escribe el timeline.
            try
            {
                await publisher.Publish(new VisitaCreadaEvent(conversacionId, visita.Id), cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo registrar el evento de timeline para la visita {VisitaId}.", visita.Id);
            }
        }

        return Result.Exito(visita.Id);
    }

    private async Task<SugerenciaVisitaCorreo?> ObtenerSugerenciaDentroDeAlcanceAsync(Guid sugerenciaId, CancellationToken cancellationToken)
    {
        var mensajeId = await comunicacionesContext.SugerenciasVisitaCorreo
            .Where(s => s.Id == sugerenciaId)
            .Select(s => (Guid?)s.MensajeId)
            .FirstOrDefaultAsync(cancellationToken);

        if (mensajeId is not { } id || !await alcanceDatos.MensajeVisibleAsync(comunicacionesContext, id, cancellationToken))
            return null;

        return await sugerenciaRepositorio.ObtenerPorIdAsync(sugerenciaId, cancellationToken);
    }

    private static Result<Guid> CentroNoEncontrado() =>
        Result.Fallo<Guid>(Error.Crear("Visita.CentroNoEncontrado", "No encontramos este centro."));
}
