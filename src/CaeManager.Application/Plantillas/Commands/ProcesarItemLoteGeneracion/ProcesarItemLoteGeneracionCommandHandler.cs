using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;

namespace CaeManager.Application.Plantillas.Commands.ProcesarItemLoteGeneracion;

public class ProcesarItemLoteGeneracionCommandHandler(
    IItemGeneracionDocumentoRepository itemRepositorio,
    ILoteGeneracionDocumentoRepository loteRepositorio,
    IMediator mediator,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ProcesarItemLoteGeneracionCommand, Result>
{
    public async Task<Result> Handle(ProcesarItemLoteGeneracionCommand request, CancellationToken cancellationToken)
    {
        var item = await itemRepositorio.ObtenerPorIdAsync(request.ItemGeneracionDocumentoId, cancellationToken);
        if (item is null)
            return Result.Fallo(Error.Crear("Plantilla.ItemNoEncontrado", "No encontramos este elemento del lote."));
        if (item.Estado != EstadoItemGeneracion.Pendiente)
            return Result.Fallo(Error.Crear("Plantilla.ItemYaProcesado", "Este elemento ya se procesó."));

        var lote = await loteRepositorio.ObtenerPorIdAsync(item.LoteGeneracionDocumentoId, cancellationToken);
        if (lote is null)
            return Result.Fallo(Error.Crear("Plantilla.LoteNoEncontrado", "No encontramos el lote de este elemento."));

        var contexto = string.IsNullOrWhiteSpace(lote.ContextoJson)
            ? new ContextoLoteGeneracionDto(null, [])
            : JsonSerializer.Deserialize<ContextoLoteGeneracionDto>(lote.ContextoJson) ?? new ContextoLoteGeneracionDto(null, []);

        var resultado = await mediator.Send(new GenerarDocumentoIndividualCommand(
            lote.PlantillaDocumentoVersionId, item.TrabajadorId, contexto.CentroId,
            contexto.ValoresManuales.Count == 0 ? null : contexto.ValoresManuales), cancellationToken);

        var ahoraUtc = DateTime.UtcNow;
        if (resultado.EsExitoso)
        {
            item.MarcarCompletado(resultado.Valor.DocumentoGeneradoId);
            lote.RegistrarItemCompletado(ahoraUtc);
        }
        else
        {
            item.MarcarFallido(resultado.Error.Mensaje);
            lote.RegistrarItemFallido(ahoraUtc);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
