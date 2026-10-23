using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.ContratosAcceso;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.ContratosAcceso.Commands.ActualizarContratoAcceso;

/// <summary>Solo cambian el fin contratado y las notas: las coordenadas y el inicio definen el contrato.</summary>
public record ActualizarContratoAccesoCommand(Guid Id, DateOnly? FechaFin, string? Notas, Guid Version = default) : ICommand;

public class ActualizarContratoAccesoCommandValidator : AbstractValidator<ActualizarContratoAccesoCommand>
{
    public ActualizarContratoAccesoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Notas).MaximumLength(ContratoAcceso.LongitudMaximaNotas);
    }
}

public class ActualizarContratoAccesoCommandHandler(
    IContratoAccesoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<ActualizarContratoAccesoCommand, Result>
{
    public async Task<Result> Handle(ActualizarContratoAccesoCommand request, CancellationToken cancellationToken)
    {
        var contrato = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("ContratoAcceso.NoEncontrado", "El contrato de acceso no existe o no tienes acceso."));

        if (ConcurrenciaOptimista.Verificar(contrato, request.Version, "este contrato de acceso") is { } conflicto)
            return Result.Fallo(conflicto);

        try
        {
            contrato.Actualizar(request.FechaFin, request.Notas);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("ContratoAcceso.FechaFinInvalida", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
