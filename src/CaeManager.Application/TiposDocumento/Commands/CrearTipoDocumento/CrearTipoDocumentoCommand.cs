using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.TiposDocumento.Commands.CrearTipoDocumento;

public record CrearTipoDocumentoCommand(
    string Nombre,
    int? VigenciaMeses,
    bool AplicaVencimientoAutomatico,
    int Orden,
    AmbitoAplicacion AmbitoAplicacion,
    bool EsObligatorio,
    string? Notas,
    string? Descripcion,
    string? CriteriosValidacion,
    string? SeSolicitaA,
    string? Observaciones,
    IReadOnlyList<Guid> CentroIds) : IRequest<Result<Guid>>;

public class CrearTipoDocumentoCommandValidator : AbstractValidator<CrearTipoDocumentoCommand>
{
    public CrearTipoDocumentoCommandValidator()
    {
        RuleFor(c => c.Nombre)
            .NotEmpty().WithMessage("El nombre es obligatorio.")
            .MaximumLength(TipoDocumento.LongitudMaximaNombre);

        RuleFor(c => c.VigenciaMeses)
            .GreaterThan(0).WithMessage("La vigencia debe ser mayor que cero.")
            .When(c => c.VigenciaMeses is not null);

        RuleFor(c => c.VigenciaMeses)
            .NotNull().WithMessage("Un tipo de documento con vencimiento automático debe tener una vigencia en meses.")
            .When(c => c.AplicaVencimientoAutomatico);

        RuleFor(c => c.Notas).MaximumLength(TipoDocumento.LongitudMaximaNotas);
        RuleFor(c => c.Descripcion).MaximumLength(TipoDocumento.LongitudMaximaDescripcion);
        RuleFor(c => c.CriteriosValidacion).MaximumLength(TipoDocumento.LongitudMaximaCriteriosValidacion);
        RuleFor(c => c.SeSolicitaA).MaximumLength(TipoDocumento.LongitudMaximaSeSolicitaA);
        RuleFor(c => c.Observaciones).MaximumLength(TipoDocumento.LongitudMaximaObservaciones);
    }
}

public class CrearTipoDocumentoCommandHandler(
    ITipoDocumentoRepository repositorio, ITipoDocumentoCentroRepository tipoDocumentoCentroRepositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearTipoDocumentoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearTipoDocumentoCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteConNombreAsync(request.Nombre, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("TipoDocumento.NombreDuplicado", "Ya existe un tipo de documento con este nombre."));

        var tipoDocumento = new TipoDocumento(
            request.Nombre,
            request.VigenciaMeses,
            request.AplicaVencimientoAutomatico,
            request.Orden,
            request.AmbitoAplicacion,
            request.EsObligatorio,
            request.Notas,
            request.Descripcion,
            request.CriteriosValidacion,
            request.SeSolicitaA,
            request.Observaciones);

        repositorio.Agregar(tipoDocumento);

        // Los Centros donde aplica solo tienen sentido para el ámbito
        // Trabajador — un Cliente/Empresa no depende de un Centro concreto.
        if (request.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
        {
            foreach (var centroId in request.CentroIds.Distinct())
                tipoDocumentoCentroRepositorio.Agregar(new TipoDocumentoCentro(tipoDocumento.Id, centroId));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(tipoDocumento.Id);
    }
}
