using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Facturacion;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Facturacion.Commands.CrearTarifaCliente;

public record CrearTarifaClienteCommand(
    Guid ClienteId,
    ConceptoFacturable Concepto,
    decimal PrecioUnitario,
    string MonedaIso = "EUR")
    : ICommand<Guid>;

public class CrearTarifaClienteCommandValidator : AbstractValidator<CrearTarifaClienteCommand>
{
    public CrearTarifaClienteCommandValidator()
    {
        RuleFor(c => c.ClienteId).NotEmpty().WithMessage("El cliente es obligatorio.");
        RuleFor(c => c.PrecioUnitario)
            .GreaterThanOrEqualTo(0).WithMessage("El precio no puede ser negativo.");
        RuleFor(c => c.MonedaIso)
            .NotEmpty()
            .Length(TarifaCliente.LongitudMonedaIso)
            .WithMessage("La moneda debe ser un código ISO de 3 caracteres (p. ej. EUR, USD).");
    }
}

public class CrearTarifaClienteCommandHandler(
    ITarifaClienteRepository repositorio,
    IClientesQueryContext clientesContext,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearTarifaClienteCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearTarifaClienteCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (!await clientesContext.Clientes.AnyAsync(c => c.Id == request.ClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("TarifaCliente.ClienteNoEncontrado", "No encontramos este cliente."));

        if (await repositorio.ExisteParaConceptoAsync(request.ClienteId, request.Concepto, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "TarifaCliente.ConceptoDuplicado",
                "Ya existe una tarifa para este concepto en el cliente seleccionado."));

        var tarifa = TarifaCliente.Crear(request.ClienteId, request.Concepto, request.PrecioUnitario, request.MonedaIso);
        repositorio.Agregar(tarifa);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(tarifa.Id);
    }
}
