using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.ContratosAcceso;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.ContratosAcceso.Commands.ReabrirContratoAcceso;

/// <summary>Deshace un <see cref="CerrarContratoAcceso.CerrarContratoAccesoCommand"/>: el contrato vuelve a contar como abierto.</summary>
public record ReabrirContratoAccesoCommand(Guid Id, Guid Version = default) : ICommand;

public class ReabrirContratoAccesoCommandValidator : AbstractValidator<ReabrirContratoAccesoCommand>
{
    public ReabrirContratoAccesoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class ReabrirContratoAccesoCommandHandler(
    IContratoAccesoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<ReabrirContratoAccesoCommand, Result>
{
    public async Task<Result> Handle(ReabrirContratoAccesoCommand request, CancellationToken cancellationToken)
    {
        var contrato = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("ContratoAcceso.NoEncontrado", "El contrato de acceso no existe o no tienes acceso."));

        if (ConcurrenciaOptimista.Verificar(contrato, request.Version, "este contrato de acceso") is { } conflicto)
            return Result.Fallo(conflicto);

        if (contrato.EstaAbierto)
            return Result.Fallo(Error.Crear("ContratoAcceso.NoCerrado", "Este contrato de acceso no está cerrado."));

        contrato.Reabrir();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
