using CaeManager.Application.Common;
using CaeManager.Application.Inbound.CatalogoBase;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.Requisitos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.Requisitos.Commands.EditarRequisitoPlantilla;

/// <summary>
/// Edita los atributos de un requisito de plantilla. Ni el tipo de documento ni el requisito de base que adopta cambian:
/// los dos se toman del requisito guardado, no de quien llama. Solo vale para lo que se pida a partir de ahora: el
/// «también los N ya dados de alta» de la tarjeta 9 es la revalidación (B-23).
/// </summary>
[AreaDelEncargo("TiposDocumento")]
public record EditarRequisitoPlantillaCommand(
    Guid PlantillaId, Guid RequisitoId, AtributosRequisito Atributos, Guid Version = default) : ICommand;

public class EditarRequisitoPlantillaCommandValidator : AbstractValidator<EditarRequisitoPlantillaCommand>
{
    public EditarRequisitoPlantillaCommandValidator()
    {
        RuleFor(c => c.PlantillaId).NotEmpty();
        RuleFor(c => c.RequisitoId).NotEmpty();
        RuleFor(c => c.Version).NotEmpty().WithMessage(PlantillaRequisitosErrores.MensajeVersionObligatoria);
        RuleFor(c => c.Atributos).NotNull().SetValidator(new AtributosRequisitoValidator());
    }
}

public class EditarRequisitoPlantillaCommandHandler(
    IPlantillaRequisitosRepository repositorio, ITipoDocumentoRepository tipos, ICatalogoBaseQueryContext catalogo,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarRequisitoPlantillaCommand, Result>
{
    public async Task<Result> Handle(EditarRequisitoPlantillaCommand request, CancellationToken cancellationToken)
    {
        var plantilla = await repositorio.ObtenerPorIdAsync(request.PlantillaId, cancellationToken);
        // shortcut: autoriza con IAlcanceDatosService sin servicio CAE Inbound, sustituir por IAutorizacionInbound en B-03
        if (plantilla is null || !await PlantillaRequisitosAutorizacion.PuedeEscribirAsync(plantilla, alcanceDatos, cancellationToken))
            return Result.Fallo(PlantillaRequisitosErrores.NoEncontrada);

        if (ConcurrenciaOptimista.Verificar(plantilla, request.Version, "esta plantilla de requisitos") is { } conflicto)
            return Result.Fallo(conflicto);

        var requisito = plantilla.Requisitos.FirstOrDefault(r => r.Id == request.RequisitoId);
        if (requisito is null)
            return Result.Fallo(PlantillaRequisitosErrores.NoEncontrada);

        var (tipo, requisitoBase, error) = await CargaRequisito.CargarAsync(
            tipos, catalogo, requisito.TipoDocumentoId, requisito.RequisitoBaseId, cancellationToken);
        if (error is not null)
            return Result.Fallo(error);

        try
        {
            plantilla.EditarRequisito(request.RequisitoId, tipo!, requisitoBase, request.Atributos);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Result.Fallo(PlantillaRequisitosErrores.Invalido(ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
