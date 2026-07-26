using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Facturacion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Facturacion.Commands.ActualizarTarifaCliente;

public record ActualizarTarifaClienteCommand(Guid Id, decimal PrecioUnitario, string MonedaIso)
    : IRequest<Result>;

public class ActualizarTarifaClienteCommandValidator : AbstractValidator<ActualizarTarifaClienteCommand>
{
    public ActualizarTarifaClienteCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.PrecioUnitario)
            .GreaterThanOrEqualTo(0).WithMessage("El precio no puede ser negativo.");
        RuleFor(c => c.MonedaIso)
            .NotEmpty()
            .Length(TarifaCliente.LongitudMonedaIso)
            .WithMessage("La moneda debe ser un código ISO de 3 caracteres (p. ej. EUR, USD).");
    }
}

public class ActualizarTarifaClienteCommandHandler(
    ITarifaClienteRepository repositorio,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ActualizarTarifaClienteCommand, Result>
{
    public async Task<Result> Handle(ActualizarTarifaClienteCommand request, CancellationToken cancellationToken)
    {
        var tarifa = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (tarifa is null)
            return Result.Fallo(Error.Crear("TarifaCliente.NoEncontrada", "La tarifa no existe o no tienes acceso."));

        tarifa.Actualizar(request.PrecioUnitario, request.MonedaIso);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
