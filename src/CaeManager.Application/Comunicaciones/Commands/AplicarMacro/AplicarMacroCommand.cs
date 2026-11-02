using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.AplicarMacro;

public record AplicarMacroCommand(Guid ConversacionId, Guid MacroId) : ICommand<string>;

public class AplicarMacroCommandHandler(
    IComunicacionesQueryContext comunicaciones,
    RellenoMacroRespuesta relleno,
    IAlcanceDatosService alcanceDatos,
    IEventoConversacionRepository eventos,
    IUnitOfWork unitOfWork) : IRequestHandler<AplicarMacroCommand, Result<string>>
{
    public async Task<Result<string>> Handle(AplicarMacroCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await comunicaciones.Conversaciones.FirstOrDefaultAsync(c => c.Id == request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion, cancellationToken))
            return Result.Fallo<string>(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        var cuerpo = await relleno.RellenarAsync(conversacion, request.MacroId, cancellationToken);
        if (cuerpo.EsFallido)
            return cuerpo;

        eventos.Agregar(new EventoConversacion(conversacion.Id, TipoEventoConversacion.PlantillaAplicada, request.MacroId, DateTime.UtcNow));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return cuerpo;
    }
}
