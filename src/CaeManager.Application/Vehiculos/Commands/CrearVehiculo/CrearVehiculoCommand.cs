using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Vehiculos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Vehiculos.Commands.CrearVehiculo;

/// <summary>
/// Un vehículo pertenece a una Empresa o a una Subcontrata, nunca ambas
/// (ver <see cref="Vehiculo.DeEmpresa"/>/<see cref="Vehiculo.DeSubcontrata"/>) —
/// exactamente uno de EmpresaId/SubcontrataId debe venir informado.
/// </summary>
public record CrearVehiculoCommand(
    Guid? EmpresaId,
    Guid? SubcontrataId,
    string Nombre,
    string Modelo,
    string NumeroPlaca) : ICommand<Guid>;

public class CrearVehiculoCommandValidator : AbstractValidator<CrearVehiculoCommand>
{
    public CrearVehiculoCommandValidator()
    {
        RuleFor(c => c)
            .Must(c => (c.EmpresaId is not null) ^ (c.SubcontrataId is not null))
            .WithMessage("Selecciona una empresa o una subcontrata (no ambas).");

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

public class CrearVehiculoCommandHandler(IVehiculoRepository repositorio, IApplicationDbContext dbContext, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearVehiculoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearVehiculoCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (request.EmpresaId is { } empresaId
            && !await dbContext.Empresas.AnyAsync(e => e.Id == empresaId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Vehiculo.EmpresaNoEncontrada", "No encontramos esta empresa."));

        if (request.SubcontrataId is { } subcontrataId
            && !await dbContext.Subcontratas.AnyAsync(s => s.Id == subcontrataId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Vehiculo.SubcontrataNoEncontrada", "No encontramos esta subcontrata."));

        if (await repositorio.ExisteConMatriculaAsync(request.NumeroPlaca, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Vehiculo.MatriculaDuplicada", "Ya existe un vehículo con este número de placa."));

        var vehiculo = request.EmpresaId is not null
            ? Vehiculo.DeEmpresa(request.EmpresaId.Value, request.Nombre, request.Modelo, request.NumeroPlaca)
            : Vehiculo.DeSubcontrata(request.SubcontrataId!.Value, request.Nombre, request.Modelo, request.NumeroPlaca);

        repositorio.Agregar(vehiculo);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(vehiculo.Id);
    }
}
