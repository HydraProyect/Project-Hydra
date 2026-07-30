using CaeManager.Application.Common;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.CrearMacro;

public record CrearMacroCommand(string Titulo, string CuerpoHtml, Guid? ClienteId) : IRequest<Result<Guid>>;

public class CrearMacroCommandValidator : AbstractValidator<CrearMacroCommand>
{
    public CrearMacroCommandValidator()
    {
        RuleFor(c => c.Titulo).NotEmpty().MaximumLength(MacroRespuesta.LongitudMaximaTitulo)
            .WithMessage($"El título no puede superar {MacroRespuesta.LongitudMaximaTitulo} caracteres.");
        RuleFor(c => c.CuerpoHtml).NotEmpty().WithMessage("El contenido de la macro es obligatorio.");
    }
}

public class CrearMacroCommandHandler(
    IMacroRespuestaRepository repositorio, IClienteRepository clienteRepositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearMacroCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearMacroCommand request, CancellationToken cancellationToken)
    {
        if (request.ClienteId is not null)
        {
            var cliente = await clienteRepositorio.ObtenerPorIdAsync(request.ClienteId.Value, cancellationToken);
            if (cliente is null)
                return Result.Fallo<Guid>(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));
        }

        var macro = new MacroRespuesta(request.Titulo, request.CuerpoHtml, request.ClienteId);
        repositorio.Agregar(macro);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(macro.Id);
    }
}
