using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Centros.Commands.CrearCentro;

public record CrearCentroCommand(
    Guid ClienteId,
    Guid EmpresaId,
    string Nombre,
    string? CodigoCentro,
    string? Direccion,
    string? Contacto,
    DateOnly? ContratoVigenteHasta) : IRequest<Result<Guid>>;

public class CrearCentroCommandValidator : AbstractValidator<CrearCentroCommand>
{
    public CrearCentroCommandValidator()
    {
        RuleFor(c => c.ClienteId).NotEmpty().WithMessage("Selecciona un cliente.");
        RuleFor(c => c.EmpresaId).NotEmpty().WithMessage("Selecciona una empresa.");

        RuleFor(c => c.Nombre)
            .NotEmpty().WithMessage("El nombre es obligatorio.")
            .MaximumLength(Centro.LongitudMaximaNombre).WithMessage($"El nombre no puede superar {Centro.LongitudMaximaNombre} caracteres.");

        RuleFor(c => c.CodigoCentro).MaximumLength(Centro.LongitudMaximaCodigo);
        RuleFor(c => c.Direccion).MaximumLength(Centro.LongitudMaximaDireccion);
        RuleFor(c => c.Contacto).MaximumLength(Centro.LongitudMaximaContacto);
    }
}

public class CrearCentroCommandHandler(ICentroRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearCentroCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearCentroCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteConNombreEnClienteAsync(request.ClienteId, request.Nombre, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Centro.NombreDuplicado", "Este cliente ya tiene un centro con este nombre."));

        var centro = new Centro(
            request.ClienteId, request.EmpresaId, request.Nombre,
            request.CodigoCentro, request.Direccion, request.Contacto, request.ContratoVigenteHasta);

        repositorio.Agregar(centro);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(centro.Id);
    }
}
