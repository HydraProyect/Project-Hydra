using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.AsignarAliasTrabajador;

/// <summary>
/// Alta de un clic desde la sugerencia de DetectarCamposDocumentoQuery (ver
/// Trabajador.AsignarAlias) — deliberadamente sin Version: es la misma
/// concesión que MarcarRequisitoDocumentalCumplidoCommand/CompletarGestionCommand,
/// una edición de un único campo de bajo riesgo de conflicto, no el
/// formulario completo de EditarTrabajadorCommand.
/// </summary>
public record AsignarAliasTrabajadorCommand(Guid TrabajadorId, string Alias) : ICommand;

public class AsignarAliasTrabajadorCommandValidator : AbstractValidator<AsignarAliasTrabajadorCommand>
{
    public AsignarAliasTrabajadorCommandValidator()
    {
        RuleFor(c => c.TrabajadorId).NotEmpty();
        RuleFor(c => c.Alias)
            .NotEmpty().WithMessage("El alias no puede estar vacío.")
            .MaximumLength(Trabajador.LongitudMaximaAlias);
    }
}

public class AsignarAliasTrabajadorCommandHandler(
    ITrabajadorRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<AsignarAliasTrabajadorCommand, Result>
{
    public async Task<Result> Handle(AsignarAliasTrabajadorCommand request, CancellationToken cancellationToken)
    {
        var trabajador = await repositorio.ObtenerPorIdAsync(request.TrabajadorId, cancellationToken);
        if (trabajador is null || !await alcanceDatos.TrabajadorVisibleAsync(trabajador.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));

        trabajador.AsignarAlias(request.Alias);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
