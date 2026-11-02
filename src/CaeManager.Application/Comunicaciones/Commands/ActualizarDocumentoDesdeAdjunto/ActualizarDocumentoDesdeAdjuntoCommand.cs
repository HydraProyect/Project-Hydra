using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;

/// <summary>
/// Flujo "Actualizar documentación desde conversación" (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md
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
    DateOnly FechaEmision, DateOnly? FechaVencimientoManual, string? Comentarios, bool NoCaduca = false)
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
        var hoy = DiaDeNegocio.Hoy();
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
            select new { a.ArchivoUrl, a.NombreArchivo, a.EstadoReparto, m.Direccion, c.ClienteId, c.EmpresaId, c.ConexionIntegracionId, ConversacionId = c.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (adjunto is null || !await alcanceDatos.ConversacionVisibleAsync(adjunto.ClienteId, adjunto.EmpresaId, adjunto.ConexionIntegracionId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto."));

        if (adjunto.Direccion != DireccionMensaje.Entrante)
            return Result.Fallo<Guid>(Error.Crear("Adjunto.NoEntrante", "Solo se reparten los adjuntos recibidos."));

        // Repetir sobre un adjunto ya asignado renovaría el Documento con el mismo archivo.
        if (adjunto.EstadoReparto != EstadoRepartoAdjunto.Pendiente)
            return Result.Fallo<Guid>(Error.Crear("Adjunto.YaResuelto", "Este adjunto ya está resuelto."));

        // Copia a una clave propia del Documento — nunca comparte
        // ArchivoUrl con el AdjuntoMensaje de origen (ver comentario de
        // clase): renovar, firmar en campo o purgar el Documento no debe
        // poder borrar el archivo que el adjunto de la conversación sigue
        // necesitando, ni al revés.
        string archivoUrlDocumento;
        Stream flujoAdjunto;
        try { flujoAdjunto = await almacenamiento.AbrirAsync(adjunto.ArchivoUrl, cancellationToken); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException
            or InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            // Sin blob (o ilegible): error controlado, igual que VerificadorArchivoAlmacenado; no una excepción al usuario.
            return ArchivoNoValido();
        }
        // El adjunto se cierra antes de guardar la copia: retiene su reserva del presupuesto de
        // memoria hasta cerrarse, y GuardarAsync reserva por su cuenta.
        using var memoria = new MemoryStream();
        await using (var flujo = flujoAdjunto)
        {
            var buffer = new byte[81920];
            int leidos;
            while ((leidos = await flujo.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (memoria.Length + leidos > LimitesArchivoSubido.TamanoMaximoBytes)
                    return ArchivoNoValido();
                await memoria.WriteAsync(buffer.AsMemory(0, leidos), cancellationToken);
            }
        }

        if (!ValidadorFirmaArchivo.EsDocumentoValido(memoria.ToArray(), adjunto.NombreArchivo))
            return ArchivoNoValido();

        memoria.Position = 0;
        var extension = Path.GetExtension(adjunto.NombreArchivo).ToLowerInvariant();
        archivoUrlDocumento = await almacenamiento.GuardarAsync(memoria, "documento" + extension, cancellationToken);

        // "Unicidad por tipo y entidad" (§ 12.7): a diferencia del alta manual
        // (donde "editar" ya significa ir a la fila correcta y renovarla),
        // aquí no hay fila seleccionada de antemano — así que el propio
        // Command decide crear-o-renovar buscando un Documento existente del
        // mismo propietario y tipo, ninguna regla de unicidad existía antes.
        var documentoExistenteId = await documentosContext.Documentos.Operativos()
            .Where(d => d.TipoDocumentoId == request.TipoDocumentoId)
            .Where(d => (request.TrabajadorId != null && d.TrabajadorId == request.TrabajadorId)
                || (request.EmpresaId != null && d.EmpresaId == request.EmpresaId))
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);

        Guid documentoId;
        try
        {
            if (documentoExistenteId is { } idExistente)
            {
                var renovado = await mediator.Send(new RenovarDocumentoCommand(
                    idExistente, request.FechaEmision, request.FechaVencimientoManual, archivoUrlDocumento, request.Comentarios,
                    NoCaduca: request.NoCaduca),
                    cancellationToken);
                if (renovado.EsFallido)
                {
                    await EliminarCopiaHuerfanaAsync(request.AdjuntoId, archivoUrlDocumento, cancellationToken);
                    return Result.Fallo<Guid>(renovado.Error);
                }

                // Renovar con archivo crea un Documento nuevo (D8): el Id que queda en uso es el que devuelve, no el del
                // operativo que se renovó, que pasa al historial.
                documentoId = renovado.Valor;
            }
            else
            {
                var creado = await mediator.Send(new CrearDocumentoCommand(
                    request.TrabajadorId, null, request.EmpresaId, null, null,
                    request.TipoDocumentoId, request.FechaEmision, request.FechaVencimientoManual, archivoUrlDocumento, request.Comentarios,
                    request.NoCaduca),
                    cancellationToken);
                if (creado.EsFallido)
                {
                    await EliminarCopiaHuerfanaAsync(request.AdjuntoId, archivoUrlDocumento, cancellationToken);
                    return Result.Fallo<Guid>(creado.Error);
                }

                documentoId = creado.Valor;
            }
        }
        catch (PresupuestoMemoriaAgotadoException)
        {
            // Crear/RenovarDocumento verifican el archivo antes de guardar nada, y esa lectura reserva del
            // presupuesto de memoria: sin hueco, la copia no la referencia nadie. Se borra y se propaga la
            // saturación, que es transitoria; quien llama avisa de ella.
            await EliminarCopiaHuerfanaAsync(request.AdjuntoId, archivoUrlDocumento, cancellationToken);
            throw;
        }

        // Publicado DESPUÉS del commit de Crear/RenovarDocumentoCommand
        // (Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 6.5) — mejor esfuerzo, mismo
        // criterio que VisitaCreadaEvent.
        try
        {
            await publisher.Publish(new DocumentoActualizadoEvent(adjunto.ConversacionId, documentoId, request.AdjuntoId), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo registrar el evento de timeline para el documento {DocumentoId}.", documentoId);
        }

        return Result.Exito(documentoId);
    }

    private static Result<Guid> ArchivoNoValido() => Result.Fallo<Guid>(Error.Crear(
        "Adjunto.ArchivoNoValido", ValidadorFirmaArchivo.MensajeDocumentoNoValido));

    // La copia se hace antes de delegar, así que si Crear/RenovarDocumento
    // rechaza (validación, alcance de cartera, concurrencia) nadie la
    // referencia: se borra para no dejar un blob huérfano por cada intento.
    // Best-effort, mismo criterio que AgregarVersionPlantilla: un fallo al
    // limpiar no debe tapar el error real que se devuelve al llamador.
    // El aviso identifica el adjunto por Id y nombra el tipo de la excepción: ni la clave del fichero en el
    // almacenamiento ni el mensaje de la excepción (que suele repetirla) deben acabar en los registros.
    internal async Task EliminarCopiaHuerfanaAsync(Guid adjuntoId, string archivoUrl, CancellationToken cancellationToken)
    {
        try
        {
            await almacenamiento.EliminarAsync(archivoUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("No se pudo borrar la copia huérfana del adjunto {AdjuntoId} ({TipoExcepcion}).",
                adjuntoId, ex.GetType().Name);
        }
    }
}
