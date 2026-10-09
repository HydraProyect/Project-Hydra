using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Commands.EnviarPaqueteAcreditacionVisita;

/// <summary>
/// Envía por correo el paquete de acreditación de una Visita y la deja con la documentación
/// gestionada (decisión del propietario, 2026-10-09): es la primera de las dos salidas de
/// «Por gestionar»; la otra es <c>MarcarDocumentacionGestionadaCommand</c>.
/// <para>
/// El envío en sí es el de <see cref="EnviarMensajeNuevoCommand"/>, con todas sus reglas de
/// buzón y de alcance. Lo que este comando añade es la Visita: se comprueba ANTES de enviar
/// que quien envía tiene alcance de gestión sobre su Centro, que no está cancelada y que no
/// cambió desde que se preparó el paquete (<paramref name="VersionVisita"/>). Si cambió, no
/// se envía: el adjunto se construyó para quienes entraban entonces, y saldría sin el
/// Trabajador que se acaba de añadir.
/// </para>
/// <para>
/// El adjunto llega de la pantalla, que lo obtuvo de <c>ObtenerPaqueteDocumentalVisitaQuery</c>
/// (la que autoriza la entrega y registra el acceso a documentos sensibles). Este comando no
/// vuelve a construirlo, así que no puede probar que lo enviado sea ese zip: la marca no
/// concede nada que <c>MarcarDocumentacionGestionadaCommand</c> no conceda ya a la misma
/// persona, con la misma autorización.
/// </para>
/// </summary>
public record EnviarPaqueteAcreditacionVisitaCommand(
    Guid VisitaId, Guid VersionVisita, Guid ConexionIntegracionId, IReadOnlyList<string> Destinatarios,
    string Asunto, string CuerpoHtml, IReadOnlyList<AdjuntoParaEnviarDto> Adjuntos) : ICommand<Guid>;

public class EnviarPaqueteAcreditacionVisitaCommandValidator : AbstractValidator<EnviarPaqueteAcreditacionVisitaCommand>
{
    public EnviarPaqueteAcreditacionVisitaCommandValidator()
    {
        RuleFor(c => c.VisitaId).NotEmpty();
        RuleFor(c => c.Adjuntos).NotEmpty().WithMessage("El envío del paquete de acreditación debe llevar el paquete adjunto.");
    }
}

public class EnviarPaqueteAcreditacionVisitaCommandHandler(
    IVisitaRepository repositorio, IAlcanceDatosService alcanceDatos, ISender mediator, IUnitOfWork unitOfWork,
    ILogger<EnviarPaqueteAcreditacionVisitaCommandHandler> logger)
    : IRequestHandler<EnviarPaqueteAcreditacionVisitaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(EnviarPaqueteAcreditacionVisitaCommand request, CancellationToken cancellationToken)
    {
        // La Visita es una coordenada que llega del cliente, no autoridad: sin alcance de
        // gestión sobre su Centro no hay ni envío ni marca, y se responde como inexistente.
        var visita = await repositorio.ObtenerPorIdAsync(request.VisitaId, cancellationToken);
        if (visita is null || !await AutorizacionCancelacionVisita.PuedeGestionarAsync(alcanceDatos, visita, cancellationToken))
            return Result.Fallo<Guid>(AutorizacionCancelacionVisita.NoEncontrada);

        // FS-11: una Visita cancelada no se modifica; primero se reactiva.
        if (visita.EstaCancelada)
            return Result.Fallo<Guid>(Error.Crear("Visita.Cancelada", "Esta visita está cancelada. Reactívala antes de modificarla."));

        if (ConcurrenciaOptimista.Verificar(visita, request.VersionVisita, "esta visita") is { } conflicto)
            return Result.Fallo<Guid>(conflicto);

        var envio = await mediator.Send(
            new EnviarMensajeNuevoCommand(
                request.ConexionIntegracionId, request.Destinatarios, request.Asunto, request.CuerpoHtml, Adjuntos: request.Adjuntos),
            cancellationToken);
        if (envio.EsFallido)
            return envio;

        // El correo ya salió y su conversación ya está guardada. Si la marca no entra (alguien
        // tocó la Visita en este instante), el envío no se da por fallido: la Visita se queda
        // «Por gestionar», que es el lado seguro, y se puede marcar a mano.
        try
        {
            visita.MarcarDocumentacionGestionada(DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogWarning(ex, "El paquete de la visita {VisitaId} se envió, pero no se pudo marcar su documentación como gestionada.", visita.Id);
        }

        return envio;
    }
}
