using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.CrearTrabajador;

/// <summary>
/// Un trabajador pertenece a una Empresa o a una Subcontrata, nunca ambas
/// (ver <see cref="Trabajador.DeEmpresa"/>/<see cref="Trabajador.DeSubcontrata"/>) —
/// exactamente uno de EmpresaId/SubcontrataId debe venir informado.
/// </summary>
public record CrearTrabajadorCommand(
    Guid? EmpresaId,
    Guid? SubcontrataId,
    string Nombre,
    string Apellidos,
    string Dni,
    DateOnly? FechaNacimiento,
    string? Email,
    string? Observaciones) : IRequest<Result<Guid>>;

public class CrearTrabajadorCommandValidator : AbstractValidator<CrearTrabajadorCommand>
{
    public CrearTrabajadorCommandValidator()
    {
        RuleFor(c => c)
            .Must(c => (c.EmpresaId is not null) ^ (c.SubcontrataId is not null))
            .WithMessage("Selecciona una empresa o una subcontrata (no ambas).");

        RuleFor(c => c.Nombre)
            .NotEmpty().WithMessage("El nombre es obligatorio.")
            .MaximumLength(Trabajador.LongitudMaximaNombre);

        RuleFor(c => c.Apellidos)
            .NotEmpty().WithMessage("Los apellidos son obligatorios.")
            .MaximumLength(Trabajador.LongitudMaximaApellidos);

        RuleFor(c => c.Dni)
            .NotEmpty().WithMessage("El documento de identidad es obligatorio.")
            .Length(Trabajador.LongitudMinimaDni, Trabajador.LongitudMaximaDni)
            .WithMessage($"El documento de identidad debe tener entre {Trabajador.LongitudMinimaDni} y {Trabajador.LongitudMaximaDni} caracteres.")
            .Must(TenerDigitoDeControlValido)
            .WithMessage("El dígito de control no es válido para un DNI, NIE o CIF.");

        RuleFor(c => c.Email)
            .MaximumLength(Trabajador.LongitudMaximaEmail)
            .EmailAddress().WithMessage("Introduce un correo válido.")
            .When(c => !string.IsNullOrWhiteSpace(c.Email));

        RuleFor(c => c.Observaciones).MaximumLength(Trabajador.LongitudMaximaObservaciones);
    }

    private static bool TenerDigitoDeControlValido(string dni)
    {
        var resultado = ValidadorIdentificacion.Analizar(dni);
        return resultado.EsValido
            || resultado.Tipo is not (TipoIdentificacion.Dni or TipoIdentificacion.Nie or TipoIdentificacion.NifEmpresa);
    }
}

public class CrearTrabajadorCommandHandler(ITrabajadorRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearTrabajadorCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearTrabajadorCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteConDniAsync(request.Dni, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Trabajador.DniDuplicado", "Ya existe un trabajador con este DNI."));

        var trabajador = request.EmpresaId is not null
            ? Trabajador.DeEmpresa(
                request.EmpresaId.Value, request.Nombre, request.Apellidos, request.Dni,
                request.FechaNacimiento, request.Email, request.Observaciones)
            : Trabajador.DeSubcontrata(
                request.SubcontrataId!.Value, request.Nombre, request.Apellidos, request.Dni,
                request.FechaNacimiento, request.Email, request.Observaciones);

        repositorio.Agregar(trabajador);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(trabajador.Id);
    }
}
