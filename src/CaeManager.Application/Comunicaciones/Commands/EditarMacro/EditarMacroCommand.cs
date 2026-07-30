using CaeManager.Application.Common;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.EditarMacro;

public record EditarMacroCommand(Guid Id, string Titulo, string CuerpoHtml, Guid? ClienteId) : IRequest<Result>;

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
    IMacroRespuestaRepository repositorio, IClienteRepository clienteRepositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarMacroCommand, Result>
{
    public async Task<Result> Handle(EditarMacroCommand request, CancellationToken cancellationToken)
    {
        var macro = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (macro is null)
            return Result.Fallo(Error.Crear("MacroRespuesta.NoEncontrada", "No encontramos esta macro."));

        if (request.ClienteId is not null)
        {
            var cliente = await clienteRepositorio.ObtenerPorIdAsync(request.ClienteId.Value, cancellationToken);
            if (cliente is null)
                return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));
        }

        macro.Actualizar(request.Titulo, request.CuerpoHtml, request.ClienteId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
