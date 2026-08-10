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
    ILogger<CrearVisitaCommandHandler> logger)
    : IRequestHandler<CrearVisitaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearVisitaCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (!await centrosContext.Centros.AnyAsync(c => c.Id == request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Visita.CentroNoEncontrado", "No encontramos este centro."));

        var trabajadorIds = request.TrabajadorIds.Distinct().ToList();
        var encontrados = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => t.Id)
            .CountAsync(cancellationToken);

        if (encontrados != trabajadorIds.Count)
            return Result.Fallo<Guid>(Error.Crear("Visita.TrabajadorNoEncontrado", "Alguno de los trabajadores seleccionados no existe."));

        var origen = request.SugerenciaVisitaCorreoId is not null ? OrigenVisita.Correo : OrigenVisita.Plataforma;
        var visita = new Visita(request.CentroId, request.FechaInicio, request.FechaFin, request.Notas, origen, request.HoraEstimadaAcceso);
        repositorio.Agregar(visita);

        foreach (var trabajadorId in trabajadorIds)
            visitaTrabajadorRepositorio.Agregar(new VisitaTrabajador(visita.Id, trabajadorId));

        Guid? conversacionParaPaquete = null;
        if (request.SugerenciaVisitaCorreoId is { } sugerenciaId)
        {
            var sugerencia = await sugerenciaRepositorio.ObtenerPorIdAsync(sugerenciaId, cancellationToken);
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

            // Publicado DESPUÉS del commit (ARQUITECTURA-INTEGRACIONES.md
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
}
