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
///
/// El adjunto queda asignado en el mismo guardado que el Documento: se carga aquí, se deja la transición pendiente en
/// el contexto con un Id de Documento reservado y Crear/RenovarDocumento la confirman con su <c>SaveChangesAsync</c>.
/// El token <c>Version</c> del adjunto rige ese guardado: si otra sesión lo cambió desde esta lectura (propuesta,
/// estado u otra alta), la base rechaza el lote entero, no queda Documento y se responde
/// <see cref="ActualizarDocumentoDesdeAdjuntoCommandHandler.CodigoConflicto"/>. El adjunto guarda el destino y el tipo
/// del alta, haya o no propuesta previa: lo que elige una persona sustituye a la propuesta.
///
/// El Id se reserva antes porque el resultado llega tarde: Crear/RenovarDocumento guardan y publican
/// <c>DocumentacionCambiadaEvent</c> antes de devolverlo, de modo que escribir el Id después exigiría un segundo guardado
/// (la ventana que esto cierra) o una transacción envolvente que dispararía ese evento antes de su commit. La reserva
/// viaja en una propiedad que solo esta capa puede fijar y que ninguna serialización lee, y el Documento la toma al nacer.
///
/// Los eventos que siguen al commit también pueden lanzar o devolver un fallo, y el error no dice en qué lado del guardado
/// ocurrió, así que tras cualquier fallo decide la base. El adjunto guardado con el Id reservado prueba el commit, porque
/// viaja en el mismo guardado que el Documento y nada limpia ese campo después: el alta está hecha, aunque el Documento se
/// haya eliminado ya. Si no lo prueba, la copia del fichero solo se borra cuando tampoco hay fila con ese Id, ni entre las
/// eliminadas, que son restaurables. Lo demás es <see cref="ActualizarDocumentoDesdeAdjuntoCommandHandler.CodigoEstadoIndeterminado"/>:
/// la copia se conserva y la respuesta no afirma ni que se guardó ni que no.
///
/// <paramref name="AplicaPropuestaFijada"/> lo pone quien da el alta con la propuesta que leyó del adjunto en vez de con
/// lo que eligió una persona: si la propuesta ya es otra, es un conflicto antes de guardar nada.
/// </summary>
public record ActualizarDocumentoDesdeAdjuntoCommand(
    Guid AdjuntoId, Guid TipoDocumentoId, Guid? TrabajadorId, Guid? EmpresaId,
    DateOnly FechaEmision, DateOnly? FechaVencimientoManual, string? Comentarios, bool NoCaduca = false,
    bool AplicaPropuestaFijada = false)
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
    IComunicacionesQueryContext comunicacionesContext, IAdjuntoMensajeRepository repositorio, IAlcanceDatosService alcanceDatos,
    IDocumentosQueryContext documentosContext, IMediator mediator, IPublisher publisher,
    IFileStorageService almacenamiento, ICurrentUserService currentUserService, ITenantActual tenantActual,
    ILogger<ActualizarDocumentoDesdeAdjuntoCommandHandler> logger)
    : IRequestHandler<ActualizarDocumentoDesdeAdjuntoCommand, Result<Guid>>
{
    public const string CodigoConflicto = "Adjunto.Conflicto";
    public const string CodigoEstadoIndeterminado = "Adjunto.EstadoIndeterminado";

    private enum DesenlaceAlta { Confirmada, NoConfirmada, Indeterminada }

    public async Task<Result<Guid>> Handle(ActualizarDocumentoDesdeAdjuntoCommand request, CancellationToken cancellationToken)
    {
        var origen = await RepartoAdjuntos.ObtenerAsync(comunicacionesContext, request.AdjuntoId, cancellationToken);
        if (origen is null || !await origen.VisibleAsync(alcanceDatos, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto."));

        // Esta lectura fija el token con el que se guardará: lo que cambie desde aquí tumba la operación entera.
        var adjunto = await repositorio.ObtenerPorIdAsync(request.AdjuntoId, cancellationToken);
        if (adjunto is null)
            return Result.Fallo<Guid>(Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto."));

        // Repetir sobre un adjunto ya asignado renovaría el Documento con el mismo archivo.
        if (!adjunto.EstaPendiente)
            return Result.Fallo<Guid>(Error.Crear("Adjunto.YaResuelto", "Este adjunto ya está resuelto."));

        if (request.AplicaPropuestaFijada && !EsLaPropuestaVigente(adjunto, request))
            return Result.Fallo<Guid>(Conflicto());

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

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var documentoReservadoId = IdentificadorEntidad.Nuevo();

        // Pendiente en el contexto: lo confirma el SaveChangesAsync de Crear/RenovarDocumento. Desde aquí, toda salida
        // sin ese guardado debe olvidar el cambio, o saldría en el siguiente guardado del circuito.
        adjunto.FijarDestino(request.TrabajadorId, request.EmpresaId, request.TipoDocumentoId);
        adjunto.MarcarAsignado(documentoReservadoId, usuarioId, DateTime.UtcNow);

        Result<Guid> alta;
        try
        {
            // Renovar con archivo crea un Documento nuevo (D8), y este siempre lo trae: la copia es una clave recién
            // creada. El Id que queda en uso es el reservado, no el del operativo que se renovó y pasa al historial.
            alta = documentoExistenteId is { } idExistente
                ? await mediator.Send(new RenovarDocumentoCommand(
                    idExistente, request.FechaEmision, request.FechaVencimientoManual, archivoUrlDocumento, request.Comentarios,
                    NoCaduca: request.NoCaduca) { DocumentoIdReservado = documentoReservadoId },
                    cancellationToken)
                : await mediator.Send(new CrearDocumentoCommand(
                    request.TrabajadorId, null, request.EmpresaId, null, null,
                    request.TipoDocumentoId, request.FechaEmision, request.FechaVencimientoManual, archivoUrlDocumento, request.Comentarios,
                    request.NoCaduca) { DocumentoIdReservado = documentoReservadoId },
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Quien pidió el alta ya no espera respuesta, y no se comprueba nada con su cancelación: la copia se queda.
            repositorio.OlvidarCambios(adjunto);
            throw;
        }
        catch (Exception ex)
        {
            var desenlace = await ComprobarDesenlaceAsync(request.AdjuntoId, documentoReservadoId, cancellationToken);
            if (desenlace != DesenlaceAlta.Confirmada)
            {
                repositorio.OlvidarCambios(adjunto);
                if (desenlace == DesenlaceAlta.Indeterminada)
                {
                    // Solo el tipo: el mensaje de una excepción de almacenamiento puede traer una ruta física.
                    logger.LogError("No se pudo determinar el resultado del alta desde el adjunto {AdjuntoId} ({TipoExcepcion}).",
                        request.AdjuntoId, ex.GetType().Name);
                    return Result.Fallo<Guid>(EstadoIndeterminado());
                }

                // Validación y falta de memoria son rechazos del alta, y la base acaba de probar que no se guardó. Una
                // excepción desconocida no dice si el commit seguía en curso: esa copia se queda. Una copia huérfana se
                // puede limpiar; un Documento sin archivo, no.
                if (ex is PresupuestoMemoriaAgotadoException or ValidationException)
                    await EliminarCopiaHuerfanaAsync(request.AdjuntoId, archivoUrlDocumento, cancellationToken);
                throw;
            }

            logger.LogWarning("El alta del documento {DocumentoId} se confirmó, pero un paso posterior falló ({TipoExcepcion}).",
                documentoReservadoId, ex.GetType().Name);
            alta = Result.Exito(documentoReservadoId);
        }

        if (alta.EsFallido)
        {
            var desenlace = await ComprobarDesenlaceAsync(request.AdjuntoId, documentoReservadoId, cancellationToken);
            if (desenlace != DesenlaceAlta.Confirmada)
            {
                repositorio.OlvidarCambios(adjunto);
                if (desenlace == DesenlaceAlta.Indeterminada)
                    return Result.Fallo<Guid>(EstadoIndeterminado());

                await EliminarCopiaHuerfanaAsync(request.AdjuntoId, archivoUrlDocumento, cancellationToken);
                return Result.Fallo<Guid>(alta.Error.Codigo == ConcurrenciaOptimista.CodigoConflicto ? Conflicto() : alta.Error);
            }

            logger.LogWarning("El alta del documento {DocumentoId} se confirmó, pero un paso posterior falló ({Codigo}).",
                documentoReservadoId, alta.Error.Codigo);
            alta = Result.Exito(documentoReservadoId);
        }

        // Publicado DESPUÉS del commit de Crear/RenovarDocumentoCommand
        // (Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 6.5) — mejor esfuerzo, mismo
        // criterio que VisitaCreadaEvent.
        try
        {
            await publisher.Publish(new DocumentoActualizadoEvent(origen.ConversacionId, alta.Valor), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo registrar el evento de timeline para el documento {DocumentoId}.", alta.Valor);
        }

        return Result.Exito(alta.Valor);
    }

    private static bool EsLaPropuestaVigente(AdjuntoMensaje adjunto, ActualizarDocumentoDesdeAdjuntoCommand request) =>
        adjunto.DestinoTrabajadorId == request.TrabajadorId
        && adjunto.DestinoEmpresaId == request.EmpresaId
        && adjunto.TipoDocumentoPropuestoId == request.TipoDocumentoId;

    private static Error Conflicto() => Error.Crear(
        CodigoConflicto, "Este adjunto o su Documento cambiaron mientras se asignaba, y no se ha guardado nada. Revísalo y vuelve a intentarlo.");

    private static Error EstadoIndeterminado() => Error.Crear(
        CodigoEstadoIndeterminado, "No hemos podido comprobar si este adjunto se asignó. Revisa su estado antes de volver a intentarlo.");

    private static Result<Guid> ArchivoNoValido() => Result.Fallo<Guid>(Error.Crear(
        "Adjunto.ArchivoNoValido", ValidadorFirmaArchivo.MensajeDocumentoNoValido));

    // Pregunta a la base, no al contexto: el adjunto en memoria lleva la transición aunque no se haya guardado. Una fila
    // con el Id reservado que no prueba el alta no se explica aquí (una colisión, o algo no previsto), y por eso no se
    // da por éxito ni autoriza borrar. Sin filtro global, porque una fila eliminada es restaurable y conserva su archivo:
    // ignorarlo deja de filtrar por Tenant, así que se compara a mano, y RLS sigue aplicando debajo.
    private async Task<DesenlaceAlta> ComprobarDesenlaceAsync(Guid adjuntoId, Guid documentoId, CancellationToken cancellationToken)
    {
        try
        {
            if (await repositorio.ObtenerDocumentoGuardadoIdAsync(adjuntoId, cancellationToken) == documentoId)
                return DesenlaceAlta.Confirmada;

            if (tenantActual.TenantId is not { } tenantId)
                return DesenlaceAlta.Indeterminada;

            var hayFila = await documentosContext.Documentos
                .IgnoreQueryFilters()
                .AnyAsync(d => d.Id == documentoId && d.TenantId == tenantId, cancellationToken);

            return hayFila ? DesenlaceAlta.Indeterminada : DesenlaceAlta.NoConfirmada;
        }
        catch (Exception ex)
        {
            logger.LogWarning("No se pudo comprobar el alta del adjunto {AdjuntoId} ({TipoExcepcion}).", adjuntoId, ex.GetType().Name);
            return DesenlaceAlta.Indeterminada;
        }
    }

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
