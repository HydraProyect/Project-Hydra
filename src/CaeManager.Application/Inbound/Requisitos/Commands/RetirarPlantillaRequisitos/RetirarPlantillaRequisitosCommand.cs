using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.Requisitos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.Requisitos.Commands.RetirarPlantillaRequisitos;

/// <summary>Retira una plantilla de requisitos: deja de pedirse lo que pedía. No se borra ni se reactiva.</summary>
[AreaDelEncargo("TiposDocumento")]
public record RetirarPlantillaRequisitosCommand(Guid Id, Guid Version = default) : ICommand;

public class RetirarPlantillaRequisitosCommandValidator : AbstractValidator<RetirarPlantillaRequisitosCommand>
{
    public RetirarPlantillaRequisitosCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Version).NotEmpty().WithMessage(PlantillaRequisitosErrores.MensajeVersionObligatoria);
    }
}

public class RetirarPlantillaRequisitosCommandHandler(
    IPlantillaRequisitosRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<RetirarPlantillaRequisitosCommand, Result>
{
    public async Task<Result> Handle(RetirarPlantillaRequisitosCommand request, CancellationToken cancellationToken)
    {
        var plantilla = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (plantilla is null || !await PlantillaRequisitosAutorizacion.PuedeEscribirAsync(plantilla, alcanceDatos, cancellationToken))
            return Result.Fallo(PlantillaRequisitosErrores.NoEncontrada);

        if (ConcurrenciaOptimista.Verificar(plantilla, request.Version, "esta plantilla de requisitos") is { } conflicto)
            return Result.Fallo(conflicto);

        plantilla.Retirar();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
