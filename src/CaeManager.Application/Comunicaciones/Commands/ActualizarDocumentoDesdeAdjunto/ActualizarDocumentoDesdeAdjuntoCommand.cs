using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;

/// <summary>
/// Flujo "Actualizar documentación desde conversación" (docs/COMUNICACIONES.md
/// § 12.7), paso 4 "Aplicar y registrar". No reimplementa la lógica de
/// creación/renovación de Documento (verificación de Ámbito, cálculo de
/// vencimiento, encolado de análisis IA) — delega en <see cref="CrearDocumentoCommand"/>/
/// <see cref="RenovarDocumentoCommand"/> vía <see cref="IMediator"/>, mismo
/// patrón de "un Command orquesta otro" que <c>PedirPrioridadValidacionCommand</c>.
///
/// El contenido no se vuelve a subir desde el cliente, pero SÍ se copia a una
/// clave propia del Documento antes de delegar (auditoría módulo 6, hallazgo
/// coordinado con el módulo 2 de ingesta/almacenamiento): compartir
/// literalmente <see cref="Domain.Comunicaciones.AdjuntoMensaje.ArchivoUrl"/>
/// hacía que renovar/firmar/purgar el Documento borrara el blob del que
/// <see cref="Domain.Comunicaciones.AdjuntoMensaje"/> seguía dependiendo —
/// <c>ObtenerAdjuntoParaDescargaQuery</c> quedaba apuntando a un archivo ya
/// eliminado, sin aviso y sin vuelta atrás. Cada clave debe tener exactamente
/// una fila propietaria con autoridad para borrarla.
/// </summary>
public record ActualizarDocumentoDesdeAdjuntoCommand(
    Guid AdjuntoId, Guid TipoDocumentoId, Guid? TrabajadorId, Guid? EmpresaId,
    DateOnly FechaEmision, DateOnly? FechaVencimientoManual, string? Comentarios)
    : ICommand<Guid>;

public class ActualizarDocumentoDesdeAdjuntoCommandValidator : AbstractValidator<ActualizarDocumentoDesdeAdjuntoCommand>
{
    public ActualizarDocumentoDesdeAdjuntoCommandValidator()
    {
        RuleFor(c => c.AdjuntoId).NotEmpty();
        RuleFor(c => c.TipoDocumentoId).NotEmpty().WithMessage("Selecciona un tipo de documento.");

        RuleFor(c => c)
            .Must(c => (c.TrabajadorId is not null) ^ (c.EmpresaId is not null))
            .WithMessage("Selecciona un trabajador o una empresa (exactamente uno).");

        // "Mes actual o anterior" (§ 12.7) — distinta de la regla de día ya
        // existente en Crear/RenovarDocumentoCommand ("no futura"): esta
        // acota al mes, coherente con que TC2/Seguros RC son documentos
        // mensuales y el gestor puede estar subiendo con unos días de
        // adelanto el mes que ya terminó.
        RuleFor(c => c.FechaEmision)
            .Must(NoSerDeUnMesFuturo)
            .WithMessage("La fecha de emisión no puede ser de un mes futuro.");

        RuleFor(c => c.Comentarios).MaximumLength(Documento.LongitudMaximaComentarios);
    }

    private static bool NoSerDeUnMesFuturo(DateOnly fecha)
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        return fecha.Year < hoy.Year || (fecha.Year == hoy.Year && fecha.Month <= hoy.Month);
    }
}

public class ActualizarDocumentoDesdeAdjuntoCommandHandler(
    IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos,
    IDocumentosQueryContext documentosContext, IMediator mediator, IPublisher publisher,
    IFileStorageService almacenamiento,
    ILogger<ActualizarDocumentoDesdeAdjuntoCommandHandler> logger)
    : IRequestHandler<ActualizarDocumentoDesdeAdjuntoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ActualizarDocumentoDesdeAdjuntoCommand request, CancellationToken cancellationToken)
    {
        var adjunto = await (
            from a in comunicacionesContext.AdjuntosMensaje
            join m in comunicacionesContext.Mensajes on a.MensajeId equals m.Id
            join c in comunicacionesContext.Conversaciones on m.ConversacionId equals c.Id
            where a.Id == request.AdjuntoId
            select new { a.ArchivoUrl, a.NombreArchivo, c.ClienteId, c.ConexionIntegracionId, ConversacionId = c.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (adjunto is null || !await alcanceDatos.ConversacionVisibleAsync(adjunto.ClienteId, adjunto.ConexionIntegracionId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto."));

        // Copia a una clave propia del Documento — nunca comparte
        // ArchivoUrl con el AdjuntoMensaje de origen (ver comentario de
        // clase): renovar, firmar en campo o purgar el Documento no debe
        // poder borrar el archivo que el adjunto de la conversación sigue
        // necesitando, ni al revés.
        string archivoUrlDocumento;
        await using (var flujo = await almacenamiento.AbrirAsync(adjunto.ArchivoUrl, cancellationToken))
        {
            archivoUrlDocumento = await almacenamiento.GuardarAsync(flujo, adjunto.NombreArchivo, cancellationToken);
        }

        // "Unicidad por tipo y entidad" (§ 12.7): a diferencia del alta manual
        // (donde "editar" ya significa ir a la fila correcta y renovarla),
        // aquí no hay fila seleccionada de antemano — así que el propio
        // Command decide crear-o-renovar buscando un Documento existente del
        // mismo propietario y tipo, ninguna regla de unicidad existía antes.
        var documentoExistenteId = await documentosContext.Documentos
            .Where(d => d.TipoDocumentoId == request.TipoDocumentoId)
            .Where(d => (request.TrabajadorId != null && d.TrabajadorId == request.TrabajadorId)
                || (request.EmpresaId != null && d.EmpresaId == request.EmpresaId))
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);

        Guid documentoId;
        if (documentoExistenteId is { } idExistente)
        {
            var renovado = await mediator.Send(new RenovarDocumentoCommand(
                idExistente, request.FechaEmision, request.FechaVencimientoManual, archivoUrlDocumento, request.Comentarios),
                cancellationToken);
            if (renovado.EsFallido)
                return Result.Fallo<Guid>(renovado.Error);

            documentoId = idExistente;
        }
        else
        {
            var creado = await mediator.Send(new CrearDocumentoCommand(
                request.TrabajadorId, null, request.EmpresaId, null, null,
                request.TipoDocumentoId, request.FechaEmision, request.FechaVencimientoManual, archivoUrlDocumento, request.Comentarios),
                cancellationToken);
            if (creado.EsFallido)
                return Result.Fallo<Guid>(creado.Error);

            documentoId = creado.Valor;
        }

        // Publicado DESPUÉS del commit de Crear/RenovarDocumentoCommand
        // (ARQUITECTURA-INTEGRACIONES.md § 6.5) — mejor esfuerzo, mismo
        // criterio que VisitaCreadaEvent.
        try
        {
            await publisher.Publish(new DocumentoActualizadoEvent(adjunto.ConversacionId, documentoId), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo registrar el evento de timeline para el documento {DocumentoId}.", documentoId);
        }

        return Result.Exito(documentoId);
    }
}
