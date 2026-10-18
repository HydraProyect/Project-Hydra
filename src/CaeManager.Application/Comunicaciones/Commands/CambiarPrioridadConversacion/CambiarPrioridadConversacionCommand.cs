using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Commands.CambiarPrioridadConversacion;

public record CambiarPrioridadConversacionCommand(Guid ConversacionId, PrioridadConversacion Prioridad) : ICommand;

public class CambiarPrioridadConversacionCommandValidator : AbstractValidator<CambiarPrioridadConversacionCommand>
{
    public CambiarPrioridadConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty();
        RuleFor(c => c.Prioridad).IsInEnum();
    }
}

public class CambiarPrioridadConversacionCommandHandler(IConversacionRepository repositorio,
    IAlcanceDatosService alcanceDatos, IContextoOperativoConversacionService contexto, IUnitOfWork unitOfWork,
    ILogger<CambiarPrioridadConversacionCommandHandler> logger)
    : IRequestHandler<CambiarPrioridadConversacionCommand, Result>
{
    public async Task<Result> Handle(CambiarPrioridadConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await repositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion.ClienteId,
                conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        var operacion = request.Prioridad == PrioridadConversacion.Critica
            ? await contexto.ResolverAsync(conversacion.TenantId, conversacion.ClienteId, conversacion.EmpresaId, cancellationToken)
            : null;

        // Marcar Crítica nunca depende de resolver el Operador CAE: sin él, el aviso queda pendiente
        // (AvisoCriticaPendienteId) y el proceso de fondo lo reintenta.
        conversacion.CambiarPrioridad(request.Prioridad);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (operacion is not null && conversacion.AvisoCriticaPendienteId is { } eventoId)
        {
            try
            {
                await contexto.AvisarCriticaAsync(operacion, eventoId, cancellationToken);
                conversacion.ConfirmarAvisoCritica(eventoId);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // La prioridad ya está guardada. El proceso de fondo reintenta el evento pendiente sin duplicar avisos.
                logger.LogWarning(ex, "El aviso de prioridad Crítica {EventoId} queda pendiente de reintento.", eventoId);
            }
        }
        return Result.Exito();
    }
}
