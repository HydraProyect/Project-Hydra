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

        // DEC-5 (propietario, 2026-09-02): un obligatorio vacío no falla el ítem
        // — el documento existe— pero tampoco lo deja como limpio. Sin este
        // tercer estado el aviso solo viviría en la respuesta síncrona y un lote
        // procesado de noche llegaría a la mañana sin rastro de él.
        var ahoraUtc = DateTime.UtcNow;
        if (resultado.EsExitoso)
        {
            if (resultado.Valor.CamposObligatoriosVacios.Count > 0)
                item.MarcarCompletadoConAvisos(resultado.Valor.DocumentoGeneradoId, resultado.Valor.CamposObligatoriosVacios);
            else
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
