using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.RequisitosDocumentales;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.RequisitosDocumentales.Commands.EditarRequisitoDocumental;

public record EditarRequisitoDocumentalCommand(
    Guid Id, string Descripcion, string? PeriodicidadEspecial, bool BloqueaAcceso, string? Notas, Guid Version = default)
    : ICommand;

public class EditarRequisitoDocumentalCommandValidator : AbstractValidator<EditarRequisitoDocumentalCommand>
{
    public EditarRequisitoDocumentalCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Descripcion)
            .NotEmpty().WithMessage("La descripción del requisito es obligatoria.")
            .MaximumLength(RequisitoDocumental.LongitudMaximaDescripcion);
        RuleFor(c => c.PeriodicidadEspecial).MaximumLength(RequisitoDocumental.LongitudMaximaPeriodicidad);
        RuleFor(c => c.Notas).MaximumLength(RequisitoDocumental.LongitudMaximaNotas);
    }
}

public class EditarRequisitoDocumentalCommandHandler(
    IRequisitoDocumentalRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarRequisitoDocumentalCommand, Result>
{
    public async Task<Result> Handle(EditarRequisitoDocumentalCommand request, CancellationToken cancellationToken)
    {
        var requisito = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (requisito is null || !await alcanceDatos.CentroVisibleAsync(requisito.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("RequisitoDocumental.NoEncontrado", "El requisito no existe o no tienes acceso."));

        if (ConcurrenciaOptimista.Verificar(requisito, request.Version, "este requisito") is { } conflicto)
            return Result.Fallo(conflicto);

        requisito.Actualizar(request.Descripcion, request.PeriodicidadEspecial, request.BloqueaAcceso, request.Notas);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
