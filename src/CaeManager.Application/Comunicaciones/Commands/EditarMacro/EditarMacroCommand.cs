using CaeManager.Application.Common;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.EditarMacro;

public record EditarMacroCommand(
    Guid Id, string Titulo, string CuerpoHtml, Guid? ClienteId, Guid Version = default) : IRequest<Result>;

public class EditarMacroCommandValidator : AbstractValidator<EditarMacroCommand>
{
    public EditarMacroCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Titulo).NotEmpty().MaximumLength(MacroRespuesta.LongitudMaximaTitulo)
            .WithMessage($"El título no puede superar {MacroRespuesta.LongitudMaximaTitulo} caracteres.");
        RuleFor(c => c.CuerpoHtml).NotEmpty().WithMessage("El contenido de la macro es obligatorio.");
    }
}

public class EditarMacroCommandHandler(
    IMacroRespuestaRepository repositorio, IClienteRepository clienteRepositorio,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarMacroCommand, Result>
{
    public async Task<Result> Handle(EditarMacroCommand request, CancellationToken cancellationToken)
    {
        var macro = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        // Dos alcances distintos y los dos hacen falta (hallazgo N-3): la
        // macro que se edita, y el cliente al que se la quiere reasignar —
        // si no, se podía mover una plantilla a la cartera de otro.
        if (macro is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(macro.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("MacroRespuesta.NoEncontrada", "No encontramos esta macro."));

        if (ConcurrenciaOptimista.Verificar(macro, request.Version, "esta macro") is { } conflicto)
            return Result.Fallo(conflicto);

        if (request.ClienteId is not null)
        {
            var cliente = await clienteRepositorio.ObtenerPorIdAsync(request.ClienteId.Value, cancellationToken);
            if (cliente is null || !await alcanceDatos.ClienteVisibleAsync(cliente.Id, cancellationToken))
                return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));
        }

        macro.Actualizar(request.Titulo, request.CuerpoHtml, request.ClienteId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
