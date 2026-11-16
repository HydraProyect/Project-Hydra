using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.CatalogoBase;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.Requisitos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.Requisitos.Commands.AnadirRequisitoAPlantilla;

/// <summary>
/// Añade a una plantilla un requisito acreditado por un tipo de documento del Tenant TCE, adoptando o no uno del catálogo
/// base (tarjeta 9).
/// </summary>
[AreaDelEncargo("TiposDocumento")]
public record AnadirRequisitoAPlantillaCommand(
    Guid PlantillaId, Guid TipoDocumentoId, Guid? RequisitoBaseId, AtributosRequisito Atributos, Guid Version = default) : ICommand<Guid>, IComandoInbound;

public class AnadirRequisitoAPlantillaCommandValidator : AbstractValidator<AnadirRequisitoAPlantillaCommand>
{
    public AnadirRequisitoAPlantillaCommandValidator()
    {
        RuleFor(c => c.PlantillaId).NotEmpty();
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
        RuleFor(c => c.Version).NotEmpty().WithMessage(PlantillaRequisitosErrores.MensajeVersionObligatoria);
        RuleFor(c => c.Atributos).NotNull().SetValidator(new AtributosRequisitoValidator());
    }
}

public class AnadirRequisitoAPlantillaCommandHandler(
    IPlantillaRequisitosRepository repositorio, ITipoDocumentoRepository tipos, ICatalogoBaseQueryContext catalogo,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<AnadirRequisitoAPlantillaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AnadirRequisitoAPlantillaCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo<Guid>(AutorizacionInboundErrores.SinCapacidad);

        var plantilla = await repositorio.ObtenerPorIdAsync(request.PlantillaId, cancellationToken);
        // shortcut: autoriza con IAlcanceDatosService sin servicio CAE Inbound, sustituir por IAutorizacionInbound en B-03
        if (plantilla is null || !await PlantillaRequisitosAutorizacion.PuedeEscribirAsync(plantilla, alcanceDatos, cancellationToken))
            return Result.Fallo<Guid>(PlantillaRequisitosErrores.NoEncontrada);

        if (ConcurrenciaOptimista.Verificar(plantilla, request.Version, "esta plantilla de requisitos") is { } conflicto)
            return Result.Fallo<Guid>(conflicto);

        var (tipo, requisitoBase, error) = await CargaRequisito.CargarAsync(
            tipos, catalogo, request.TipoDocumentoId, request.RequisitoBaseId, cancellationToken);
        if (error is not null)
            return Result.Fallo<Guid>(error);

        RequisitoPlantilla requisito;
        try
        {
            requisito = plantilla.AnadirRequisito(tipo!, requisitoBase, request.Atributos);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Result.Fallo<Guid>(PlantillaRequisitosErrores.Invalido(ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(requisito.Id);
    }
}
