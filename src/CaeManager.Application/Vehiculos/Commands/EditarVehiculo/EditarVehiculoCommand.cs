using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Vehiculos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Vehiculos.Commands.EditarVehiculo;

public record EditarVehiculoCommand(
    Guid Id, string Nombre, string Modelo, string NumeroPlaca, Guid Version = default) : ICommand;

public class EditarVehiculoCommandValidator : AbstractValidator<EditarVehiculoCommand>
{
    public EditarVehiculoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.Nombre)
            .NotEmpty().WithMessage("El nombre es obligatorio.")
            .MaximumLength(Vehiculo.LongitudMaximaNombre);

        RuleFor(c => c.Modelo)
            .NotEmpty().WithMessage("El modelo es obligatorio.")
            .MaximumLength(Vehiculo.LongitudMaximaModelo);

        RuleFor(c => c.NumeroPlaca)
            .NotEmpty().WithMessage("El número de placa es obligatorio.")
            .MaximumLength(Vehiculo.LongitudMaximaNumeroPlaca);
    }
}

public class EditarVehiculoCommandHandler(IVehiculoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarVehiculoCommand, Result>
{
    public async Task<Result> Handle(EditarVehiculoCommand request, CancellationToken cancellationToken)
    {
        var vehiculo = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (vehiculo is null || !await alcanceDatos.VehiculoVisibleAsync(vehiculo.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Vehiculo.NoEncontrado", "No encontramos este vehículo."));

        if (ConcurrenciaOptimista.Verificar(vehiculo, request.Version, "este vehículo") is { } conflicto)
            return Result.Fallo(conflicto);

        if (await repositorio.ExisteConMatriculaAsync(request.NumeroPlaca, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Vehiculo.MatriculaDuplicada", "Ya existe un vehículo con este número de placa."));

        vehiculo.Actualizar(request.Nombre, request.Modelo, request.NumeroPlaca);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
