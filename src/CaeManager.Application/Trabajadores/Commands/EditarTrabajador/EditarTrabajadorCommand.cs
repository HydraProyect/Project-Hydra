using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.EditarTrabajador;

public record EditarTrabajadorCommand(
    Guid Id,
    string Nombre,
    string Apellidos,
    DateOnly? FechaNacimiento,
    string? Email,
    string? Observaciones) : IRequest<Result>;

public class EditarTrabajadorCommandValidator : AbstractValidator<EditarTrabajadorCommand>
{
    public EditarTrabajadorCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.Nombre)
            .NotEmpty().WithMessage("El nombre es obligatorio.")
            .MaximumLength(Trabajador.LongitudMaximaNombre);

        RuleFor(c => c.Apellidos)
            .NotEmpty().WithMessage("Los apellidos son obligatorios.")
            .MaximumLength(Trabajador.LongitudMaximaApellidos);

        RuleFor(c => c.Email)
            .MaximumLength(Trabajador.LongitudMaximaEmail)
            .EmailAddress().WithMessage("Introduce un correo válido.")
            .When(c => !string.IsNullOrWhiteSpace(c.Email));

        RuleFor(c => c.Observaciones).MaximumLength(Trabajador.LongitudMaximaObservaciones);
    }
}

public class EditarTrabajadorCommandHandler(ITrabajadorRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarTrabajadorCommand, Result>
{
    public async Task<Result> Handle(EditarTrabajadorCommand request, CancellationToken cancellationToken)
    {
        var trabajador = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (trabajador is null)
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));

        trabajador.Actualizar(request.Nombre, request.Apellidos, request.FechaNacimiento, request.Email, request.Observaciones);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
