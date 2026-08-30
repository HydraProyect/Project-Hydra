using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

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
    string? Observaciones,
    string? Alias = null,
    string? Telefono = null,
    string? Puesto = null) : ICommand<Guid>;

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
        RuleFor(c => c.Alias).MaximumLength(Trabajador.LongitudMaximaAlias);
        RuleFor(c => c.Telefono).MaximumLength(Trabajador.LongitudMaximaTelefono);
        RuleFor(c => c.Puesto).MaximumLength(Trabajador.LongitudMaximaPuesto);
    }

    private static bool TenerDigitoDeControlValido(string dni)
    {
        var resultado = ValidadorIdentificacion.Analizar(dni);
        return resultado.EsValido
            || resultado.Tipo is not (TipoIdentificacion.Dni or TipoIdentificacion.Nie or TipoIdentificacion.NifEmpresa);
    }
}

public class CrearTrabajadorCommandHandler(
    ITrabajadorRepository repositorio, IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearTrabajadorCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearTrabajadorCommand request, CancellationToken cancellationToken)
    {
        // Autoridad sobre el empleador, no solo existencia (auditoría Módulo
        // 5, hallazgo crítico 5/9) — ver TrabajadorAutorizacion.
        if (request.EmpresaId is { } empresaId)
        {
            if (!await empresasContext.Empresas.AnyAsync(e => e.Id == empresaId, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Trabajador.EmpresaNoEncontrada", "No encontramos esta empresa."));
            if (!await alcanceDatos.EmpresaVisibleAsync(empresaId, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Trabajador.EmpresaNoEncontrada", "No encontramos esta empresa."));
        }

        if (request.SubcontrataId is { } subcontrataId)
        {
            if (!await empresasContext.Empresas.AnyAsync(e => e.Id == subcontrataId, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Trabajador.SubcontrataNoEncontrada", "No encontramos esta subcontrata."));
            if (!await alcanceDatos.SubcontrataVisibleAsync(subcontrataId, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Trabajador.SubcontrataNoEncontrada", "No encontramos esta subcontrata."));
        }

        if (await repositorio.ExisteConDniAsync(request.Dni, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Trabajador.DniDuplicado", "Ya existe un trabajador con este DNI."));

        var trabajador = request.EmpresaId is not null
            ? Trabajador.DeEmpresa(
                request.EmpresaId.Value, request.Nombre, request.Apellidos, request.Dni,
                request.FechaNacimiento, request.Email, request.Observaciones, request.Alias, request.Telefono, request.Puesto)
            : Trabajador.DeSubcontrata(
                request.SubcontrataId!.Value, request.Nombre, request.Apellidos, request.Dni,
                request.FechaNacimiento, request.Email, request.Observaciones, request.Alias, request.Telefono, request.Puesto);

        repositorio.Agregar(trabajador);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(trabajador.Id);
    }
}
