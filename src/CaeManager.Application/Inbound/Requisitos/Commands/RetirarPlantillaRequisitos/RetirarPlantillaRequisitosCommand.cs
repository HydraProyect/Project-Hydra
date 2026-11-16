using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.Requisitos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.Requisitos.Commands.RetirarPlantillaRequisitos;

/// <summary>Retira una plantilla de requisitos: deja de pedirse lo que pedía. No se borra ni se reactiva.</summary>
[AreaDelEncargo("TiposDocumento")]
public record RetirarPlantillaRequisitosCommand(Guid Id, Guid Version = default) : ICommand, IComandoInbound;

public class RetirarPlantillaRequisitosCommandValidator : AbstractValidator<RetirarPlantillaRequisitosCommand>
{
    public RetirarPlantillaRequisitosCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Version).NotEmpty().WithMessage(PlantillaRequisitosErrores.MensajeVersionObligatoria);
    }
}

public class RetirarPlantillaRequisitosCommandHandler(
    IPlantillaRequisitosRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<RetirarPlantillaRequisitosCommand, Result>
{
    public async Task<Result> Handle(RetirarPlantillaRequisitosCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo(AutorizacionInboundErrores.SinCapacidad);

        var plantilla = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        // La capacidad (GestionarInbound) la pide arriba IAutorizacionInbound (B-03); el alcance de cartera del
        // recurso lo decide PlantillaRequisitosAutorizacion con IAlcanceDatosService, del servicio del papel activo.
        if (plantilla is null || !await PlantillaRequisitosAutorizacion.PuedeEscribirAsync(plantilla, alcanceDatos, cancellationToken))
            return Result.Fallo(PlantillaRequisitosErrores.NoEncontrada);

        if (ConcurrenciaOptimista.Verificar(plantilla, request.Version, "esta plantilla de requisitos") is { } conflicto)
            return Result.Fallo(conflicto);

        plantilla.Retirar();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
