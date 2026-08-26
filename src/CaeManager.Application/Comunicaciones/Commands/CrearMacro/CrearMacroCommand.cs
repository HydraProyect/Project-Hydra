using CaeManager.Application.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.CrearMacro;

public record CrearMacroCommand(string Titulo, string CuerpoHtml, Guid? ClienteId) : ICommand<Guid>;

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
    IMacroRespuestaRepository repositorio, IEmpresaRepository empresaRepositorio,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearMacroCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearMacroCommand request, CancellationToken cancellationToken)
    {
        if (request.ClienteId is not null)
        {
            // Mismo alcance que EditarMacroCommandHandler: sin esto se podía
            // dar de alta una plantilla en la cartera de otro gestor.
            var cliente = await empresaRepositorio.ObtenerPorIdAsync(request.ClienteId.Value, cancellationToken);
            if (cliente is null || !await alcanceDatos.ClienteVisibleAsync(cliente.Id, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));
        }

        var macro = new MacroRespuesta(request.Titulo, request.CuerpoHtml, request.ClienteId);
        repositorio.Agregar(macro);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(macro.Id);
    }
}
