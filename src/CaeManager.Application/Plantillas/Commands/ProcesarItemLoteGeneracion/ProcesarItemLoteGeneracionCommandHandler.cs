using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;

namespace CaeManager.Application.Plantillas.Commands.ProcesarItemLoteGeneracion;

public class ProcesarItemLoteGeneracionCommandHandler(
    IItemGeneracionDocumentoRepository itemRepositorio,
    ILoteGeneracionDocumentoRepository loteRepositorio,
    IPlantillasQueryContext plantillasContext,
    IDocumentosQueryContext documentosContext,
    IProyectosQueryContext proyectosContext,
    IAlcanceDatosService alcanceDatos,
    IMediator mediator,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ProcesarItemLoteGeneracionCommand, Result>
{
    public async Task<Result> Handle(ProcesarItemLoteGeneracionCommand request, CancellationToken cancellationToken)
    {
        var item = await itemRepositorio.ObtenerPorIdAsync(request.ItemGeneracionDocumentoId, cancellationToken);
        if (item is null)
            return ItemNoEncontrado();

        var lote = await loteRepositorio.ObtenerPorIdAsync(item.LoteGeneracionDocumentoId, cancellationToken);
        if (lote is null)
            return Result.Fallo(Error.Crear("Plantilla.LoteNoEncontrado", "No encontramos el lote de este elemento."));

        var contexto = string.IsNullOrWhiteSpace(lote.ContextoJson)
            ? new ContextoLoteGeneracionDto(null, [])
            : JsonSerializer.Deserialize<ContextoLoteGeneracionDto>(lote.ContextoJson) ?? new ContextoLoteGeneracionDto(null, []);

        // Asignación de Cartera por Id (OWASP ASVS 4.0.3 V4.2.1, IDOR), antes de mirar el estado del ítem y de escribir
        // nada: marcarlo fallido no tiene vuelta atrás, así que solo lo procesa quien ve el lote entero, con la misma
        // puerta que la lectura del progreso (AlcanceDeLotesGeneracion.LoteVisibleEnteroAsync: plantilla, todos sus
        // Trabajadores y el documento generado de cada ítem). El Centro del lote también tiene que ser visible:
        // GenerarDocumentoIndividual lo exige, y sin esta comprobación un Gestor CAE sin ese Centro en su Cartera seguiría
        // pudiendo dejar fallido el ítem de otro. Si no pasa, responde igual que un Id que no existe, para no delatar el
        // ítem ni su estado (ItemYaProcesado delataría que el ítem existe y ya generó).
        //
        // El ítem se leyó arriba, antes de la puerta, y la puerta mira el estado actual del lote: como un ítem solo avanza
        // y su DocumentoGeneradoId no se retira, lo que este ítem tenía al leerlo también lo ve la puerta, y se le pasa
        // además como instantánea para que su generado quede comprobado aunque la lectura actual no lo trajera (TOCTOU,
        // Codex ronda 3, PR #2222). La comprobación del estado va después de la puerta y usa esa misma lectura.
        if (!await ItemDentroDeAlcanceAsync(lote, item, contexto.CentroId, cancellationToken))
            return ItemNoEncontrado();

        if (item.Estado != EstadoItemGeneracion.Pendiente)
            return Result.Fallo(Error.Crear("Plantilla.ItemYaProcesado", "Este elemento ya se procesó."));

        Result<GenerarDocumentoIndividualResultadoDto> resultado;
        try
        {
            resultado = await mediator.Send(new GenerarDocumentoIndividualCommand(
                lote.PlantillaDocumentoVersionId, item.TrabajadorId, contexto.CentroId,
                contexto.ValoresManuales.Count == 0 ? null : contexto.ValoresManuales), cancellationToken);
        }
        catch (PresupuestoMemoriaAgotadoException)
        {
            // Sin hueco en el presupuesto de memoria de documentos (S-C-3) la generación no llegó a
            // guardar nada: el elemento queda fallido con un motivo legible en vez de pendiente para
            // siempre, y el lote sigue con los demás.
            resultado = Result.Fallo<GenerarDocumentoIndividualResultadoDto>(
                Error.Crear("Plantilla.PresupuestoMemoriaAgotado", PresupuestoMemoriaAgotadoException.MensajeUsuario));
        }

        // DEC-5 (propietario, 2026-09-02): un obligatorio vacío no falla el ítem
        // — el documento existe— pero tampoco lo deja como limpio. Sin este
        // tercer estado el aviso solo viviría en la respuesta síncrona y un lote
        // procesado de noche llegaría a la mañana sin rastro de él. DEC-32
        // (REC-115) añade la segunda categoría de aviso — un valor no reconocido
        // por el campo— con el mismo tratamiento.
        var ahoraUtc = DateTime.UtcNow;
        if (resultado.EsExitoso)
        {
            var valoresNoReconocidos = resultado.Valor.ValoresNoReconocidos
                .Select(a => $"{a.EtiquetaCampo} (recibido «{a.ValorRecibido}»)")
                .ToList();

            if (resultado.Valor.CamposObligatoriosVacios.Count > 0 || valoresNoReconocidos.Count > 0)
                item.MarcarCompletadoConAvisos(resultado.Valor.DocumentoGeneradoId, resultado.Valor.CamposObligatoriosVacios, valoresNoReconocidos);
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

    private async Task<bool> ItemDentroDeAlcanceAsync(
        LoteGeneracionDocumento lote, ItemGeneracionDocumento item, Guid? centroId, CancellationToken cancellationToken)
    {
        if (await alcanceDatos.TieneAccesoTotalAsync(cancellationToken))
            return true;

        if (centroId is { } centro && !await alcanceDatos.CentroVisibleAsync(centro, cancellationToken))
            return false;

        return await alcanceDatos.LoteVisibleEnteroAsync(
            lote.Id, lote.PlantillaDocumentoVersionId, [new ItemDeLoteLeido(item.TrabajadorId, item.DocumentoGeneradoId)],
            plantillasContext, documentosContext, proyectosContext, cancellationToken);
    }

    private static Result ItemNoEncontrado() =>
        Result.Fallo(Error.Crear("Plantilla.ItemNoEncontrado", "No encontramos este elemento del lote."));
}
