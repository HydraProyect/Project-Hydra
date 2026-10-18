using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.DescartarRecordatorioConversacion;

public record DescartarRecordatorioConversacionCommand(Guid ConversacionId, Guid PropuestaId) : ICommand;

public class DescartarRecordatorioConversacionCommandValidator : AbstractValidator<DescartarRecordatorioConversacionCommand>
{
    public DescartarRecordatorioConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("El recordatorio debe pertenecer a una conversación.");
        RuleFor(c => c.PropuestaId).NotEmpty().WithMessage("Falta el recordatorio que se descarta.");
    }
}

/// <summary>
/// El Gestor CAE descarta el recordatorio propuesto: no se envía nada y esa espera no vuelve a proponerlo. Mismo alcance
/// que responder el hilo (<see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/>); el rol lo decide
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>.
/// </summary>
public class DescartarRecordatorioConversacionCommandHandler(
    IConversacionRepository conversaciones,
    IPropuestaRecordatorioConversacionRepository propuestas,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios)
    : IRequestHandler<DescartarRecordatorioConversacionCommand, Result>
{
    public async Task<Result> Handle(DescartarRecordatorioConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await conversaciones.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(
                conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        var propuesta = await propuestas.ObtenerPorIdAsync(request.PropuestaId, cancellationToken);
        if (propuesta is null || propuesta.ConversacionId != conversacion.Id)
            return Result.Fallo(Error.Crear("Recordatorio.NoEncontrado", "No encontramos este recordatorio."));

        var descarte = propuesta.Descartar(DateTime.UtcNow);
        if (descarte.EsFallido)
            return descarte;

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            descarteCambios.DescartarCambiosPendientes();
            throw;
        }

        return Result.Exito();
    }
}
