using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.ContratosAcceso;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.ContratosAcceso.Commands.CerrarContratoAcceso;

public record CerrarContratoAccesoCommand(Guid Id, DateOnly FechaCierre, Guid Version = default) : ICommand;

public class CerrarContratoAccesoCommandValidator : AbstractValidator<CerrarContratoAccesoCommand>
{
    public CerrarContratoAccesoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class CerrarContratoAccesoCommandHandler(
    IContratoAccesoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CerrarContratoAccesoCommand, Result>
{
    public async Task<Result> Handle(CerrarContratoAccesoCommand request, CancellationToken cancellationToken)
    {
        var contrato = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("ContratoAcceso.NoEncontrado", "El contrato de acceso no existe o no tienes acceso."));

        if (ConcurrenciaOptimista.Verificar(contrato, request.Version, "este contrato de acceso") is { } conflicto)
            return Result.Fallo(conflicto);

        if (!contrato.EstaAbierto)
            return Result.Fallo(Error.Crear("ContratoAcceso.YaCerrado", "Este contrato de acceso ya está cerrado."));

        try
        {
            contrato.Cerrar(request.FechaCierre);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("ContratoAcceso.FechaCierreInvalida", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
