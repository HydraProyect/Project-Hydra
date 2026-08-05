using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.RequisitosDocumentales;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.RequisitosDocumentales.Commands.CrearRequisitoDocumental;

public record CrearRequisitoDocumentalCommand(
    Guid CentroId, string Descripcion, string? PeriodicidadEspecial, bool BloqueaAcceso, string? Notas = null,
    string? ArchivoUrl = null, string? NombreArchivoOriginal = null)
    : ICommand<Guid>;

public class CrearRequisitoDocumentalCommandValidator : AbstractValidator<CrearRequisitoDocumentalCommand>
{
    public CrearRequisitoDocumentalCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.Descripcion)
            .NotEmpty().WithMessage("La descripción del requisito es obligatoria.")
            .MaximumLength(RequisitoDocumental.LongitudMaximaDescripcion);
        RuleFor(c => c.PeriodicidadEspecial).MaximumLength(RequisitoDocumental.LongitudMaximaPeriodicidad);
        RuleFor(c => c.Notas).MaximumLength(RequisitoDocumental.LongitudMaximaNotas);
        RuleFor(c => c.ArchivoUrl).MaximumLength(RequisitoDocumental.LongitudMaximaArchivoUrl);
        RuleFor(c => c.NombreArchivoOriginal).MaximumLength(RequisitoDocumental.LongitudMaximaNombreArchivo);
    }
}

public class CrearRequisitoDocumentalCommandHandler(
    IRequisitoDocumentalRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearRequisitoDocumentalCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearRequisitoDocumentalCommand request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Centro.NoEncontrado", "El centro no existe o no tienes acceso."));

        var requisito = new RequisitoDocumental(
            request.CentroId, request.Descripcion, request.PeriodicidadEspecial, request.BloqueaAcceso, request.Notas,
            request.ArchivoUrl, request.NombreArchivoOriginal);
        repositorio.Agregar(requisito);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(requisito.Id);
    }
}
