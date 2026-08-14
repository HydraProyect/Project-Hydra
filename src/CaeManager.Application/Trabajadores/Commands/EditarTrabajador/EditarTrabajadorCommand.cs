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
    string? Observaciones,
    string? Alias = null,
    string? Telefono = null,
    string? Puesto = null,
    Guid Version = default) : ICommand;

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
        RuleFor(c => c.Alias).MaximumLength(Trabajador.LongitudMaximaAlias);
        RuleFor(c => c.Telefono).MaximumLength(Trabajador.LongitudMaximaTelefono);
        RuleFor(c => c.Puesto).MaximumLength(Trabajador.LongitudMaximaPuesto);
    }
}

public class EditarTrabajadorCommandHandler(ITrabajadorRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarTrabajadorCommand, Result>
{
    public async Task<Result> Handle(EditarTrabajadorCommand request, CancellationToken cancellationToken)
    {
        var trabajador = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (trabajador is null || !await alcanceDatos.TrabajadorVisibleAsync(trabajador.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));

        if (ConcurrenciaOptimista.Verificar(trabajador, request.Version, "este trabajador") is { } conflicto)
            return Result.Fallo(conflicto);

        trabajador.Actualizar(
            request.Nombre, request.Apellidos, request.FechaNacimiento, request.Email,
            request.Observaciones, request.Alias, request.Telefono, request.Puesto);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
