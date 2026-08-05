using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Centros.Commands.EditarCentro;

public record EditarCentroCommand(
    Guid Id,
    string Nombre,
    string? CodigoCentro,
    string? Direccion,
    string? Contacto,
    DateOnly? ContratoVigenteHasta,
    Guid Version = default) : ICommand;

public class EditarCentroCommandValidator : AbstractValidator<EditarCentroCommand>
{
    public EditarCentroCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.Nombre)
            .NotEmpty().WithMessage("El nombre es obligatorio.")
            .MaximumLength(Centro.LongitudMaximaNombre).WithMessage($"El nombre no puede superar {Centro.LongitudMaximaNombre} caracteres.");

        RuleFor(c => c.CodigoCentro).MaximumLength(Centro.LongitudMaximaCodigo);
        RuleFor(c => c.Direccion).MaximumLength(Centro.LongitudMaximaDireccion);
        RuleFor(c => c.Contacto).MaximumLength(Centro.LongitudMaximaContacto);
    }
}

public class EditarCentroCommandHandler(ICentroRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarCentroCommand, Result>
{
    public async Task<Result> Handle(EditarCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (centro is null || !await alcanceDatos.CentroVisibleAsync(centro.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro."));

        if (ConcurrenciaOptimista.Verificar(centro, request.Version, "este centro") is { } conflicto)
            return Result.Fallo(conflicto);

        if (await repositorio.ExisteConNombreEnClienteAsync(centro.ClienteId, request.Nombre, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NombreDuplicado", "Este cliente ya tiene un centro con este nombre."));

        centro.Actualizar(request.Nombre, request.CodigoCentro, request.Direccion, request.Contacto, request.ContratoVigenteHasta);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
