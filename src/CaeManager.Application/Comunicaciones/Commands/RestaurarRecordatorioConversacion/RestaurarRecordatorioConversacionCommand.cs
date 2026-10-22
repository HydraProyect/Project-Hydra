using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.RestaurarRecordatorioConversacion;

public record RestaurarRecordatorioConversacionCommand(Guid ConversacionId, Guid PropuestaId) : ICommand;

public class RestaurarRecordatorioConversacionCommandValidator : AbstractValidator<RestaurarRecordatorioConversacionCommand>
{
    public RestaurarRecordatorioConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("El recordatorio debe pertenecer a una conversación.");
        RuleFor(c => c.PropuestaId).NotEmpty().WithMessage("Falta el recordatorio que se restaura.");
    }
}

/// <summary>
/// «Deshacer» tras descartar el recordatorio propuesto: vuelve a Pendiente si la conversación sigue en la misma espera
/// (<see cref="PropuestaRecordatorioConversacion.Restaurar"/>). Mismo alcance que descartarlo
/// (<see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/>); el rol lo decide
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>.
/// </summary>
public class RestaurarRecordatorioConversacionCommandHandler(
    IConversacionRepository conversaciones,
    IPropuestaRecordatorioConversacionRepository propuestas,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios)
    : IRequestHandler<RestaurarRecordatorioConversacionCommand, Result>
{
    public async Task<Result> Handle(RestaurarRecordatorioConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await conversaciones.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(
                conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        var propuesta = await propuestas.ObtenerPorIdAsync(request.PropuestaId, cancellationToken);
        if (propuesta is null || propuesta.ConversacionId != conversacion.Id)
            return Result.Fallo(Error.Crear("Recordatorio.NoEncontrado", "No encontramos este recordatorio."));

        var restauracion = propuesta.Restaurar(conversacion);
        if (restauracion.EsFallido)
            return restauracion;

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
