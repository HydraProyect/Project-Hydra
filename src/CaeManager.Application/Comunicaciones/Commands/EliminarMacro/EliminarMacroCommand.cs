using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.EliminarMacro;

public record EliminarMacroCommand(Guid Id, Guid UsuarioId) : ICommand;

/// <summary>
/// Era el único comando del módulo sin validador (hallazgo N-13 de
/// INFORME-AUDITORIA-2.md). <c>UsuarioId</c> importa tanto como el Id de la
/// macro: es a quien se atribuye el borrado en la auditoría, así que un Guid
/// vacío deja constancia de que alguien borró algo y de que ese alguien no
/// existe.
/// </summary>
public class EliminarMacroCommandValidator : AbstractValidator<EliminarMacroCommand>
{
    public EliminarMacroCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty().WithMessage("Indica la macro que quieres eliminar.");
        RuleFor(c => c.UsuarioId).NotEmpty().WithMessage("No se pudo identificar al usuario que elimina la macro.");
    }
}

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
