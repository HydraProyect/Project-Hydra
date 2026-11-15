using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.Requisitos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.Requisitos.Commands.RetirarRequisitoPlantilla;

/// <summary>Retira un requisito de su plantilla. No se borra: lo ya pedido con él lo sigue citando.</summary>
[AreaDelEncargo("TiposDocumento")]
public record RetirarRequisitoPlantillaCommand(Guid PlantillaId, Guid RequisitoId, Guid Version = default) : ICommand;

public class RetirarRequisitoPlantillaCommandValidator : AbstractValidator<RetirarRequisitoPlantillaCommand>
{
    public RetirarRequisitoPlantillaCommandValidator()
    {
        RuleFor(c => c.PlantillaId).NotEmpty();
        RuleFor(c => c.RequisitoId).NotEmpty();
        RuleFor(c => c.Version).NotEmpty().WithMessage(PlantillaRequisitosErrores.MensajeVersionObligatoria);
    }
}

public class RetirarRequisitoPlantillaCommandHandler(
    IPlantillaRequisitosRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<RetirarRequisitoPlantillaCommand, Result>
{
    public async Task<Result> Handle(RetirarRequisitoPlantillaCommand request, CancellationToken cancellationToken)
    {
        var plantilla = await repositorio.ObtenerPorIdAsync(request.PlantillaId, cancellationToken);
        // shortcut: autoriza con IAlcanceDatosService sin servicio CAE Inbound, sustituir por IAutorizacionInbound en B-03
        if (plantilla is null || !await PlantillaRequisitosAutorizacion.PuedeEscribirAsync(plantilla, alcanceDatos, cancellationToken))
            return Result.Fallo(PlantillaRequisitosErrores.NoEncontrada);

        if (ConcurrenciaOptimista.Verificar(plantilla, request.Version, "esta plantilla de requisitos") is { } conflicto)
            return Result.Fallo(conflicto);

        try
        {
            plantilla.RetirarRequisito(request.RequisitoId);
        }
        catch (InvalidOperationException)
        {
            return Result.Fallo(PlantillaRequisitosErrores.NoEncontrada);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
