using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.EliminarMacro;

public record EliminarMacroCommand(Guid Id, Guid UsuarioId) : IRequest<Result>;

public class EliminarMacroCommandHandler(
    IMacroRespuestaRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarMacroCommand, Result>
{
    public async Task<Result> Handle(EliminarMacroCommand request, CancellationToken cancellationToken)
    {
        var macro = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        // Ver EditarMacroCommandHandler (hallazgo N-3).
        if (macro is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(macro.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("MacroRespuesta.NoEncontrada", "No encontramos esta macro."));

        macro.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
