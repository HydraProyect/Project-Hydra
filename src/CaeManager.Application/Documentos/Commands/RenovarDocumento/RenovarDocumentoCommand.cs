using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.RenovarDocumento;

/// <summary>
/// "Editar" un Documento es, en la práctica, renovarlo: nueva fecha de
/// emisión (y opcionalmente un nuevo archivo). El trabajador y el tipo de
/// documento no cambian — si son incorrectos, se elimina y se crea de nuevo
/// (ver UX_PATTERNS.md, "Cambiar estado").
///
/// <paramref name="Version"/> es la del registro tal como lo vio quien
/// renueva (llega en <c>DocumentoDetalleDto</c>) — mismo patrón que
/// <see cref="Application.Clientes.Commands.EditarCliente.EditarClienteCommand"/>.
/// <see cref="Guid.Empty"/> significa "sin comprobación", para los
/// llamadores que todavía no la propagan.
/// </summary>
public record RenovarDocumentoCommand(
    Guid Id, DateOnly FechaEmision, DateOnly? FechaVencimientoManual, string? ArchivoUrl, string? Comentarios,
    Guid Version = default) : ICommand;

public class RenovarDocumentoCommandValidator : AbstractValidator<RenovarDocumentoCommand>
{
    public RenovarDocumentoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.FechaEmision)
            .LessThanOrEqualTo(DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("La fecha de emisión no puede ser futura.");
        RuleFor(c => c.Comentarios).MaximumLength(Documento.LongitudMaximaComentarios);
    }
}

public class RenovarDocumentoCommandHandler(
    IDocumentoRepository repositorio, ITiposDocumentoQueryContext dbContext,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    ITrabajoAnalisisDocumentoRepository colaAnalisis, ICurrentUserService currentUserService,
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio,
    IPublisher publisher, IUnitOfWork unitOfWork,
    IFileStorageService almacenamiento, ILogger<RenovarDocumentoCommandHandler> logger)
    : IRequestHandler<RenovarDocumentoCommand, Result>
{
    public async Task<Result> Handle(RenovarDocumentoCommand request, CancellationToken cancellationToken)
    {
        var documento = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);

        // Se captura antes de adjuntar el nuevo: AdjuntarArchivo sobreescribe
        // ArchivoUrl y la clave anterior se perdería para siempre. Sin ella, el
        // PDF de la versión anterior —datos médicos, art. 9 RGPD— se queda en
        // almacenamiento sin que ninguna fila lo nombre: la retención no lo
        // alcanza y ninguna purga puede encontrarlo. Cada renovación dejaba una
        // copia fuera del ciclo de vida.
        var archivoAnterior = documento?.ArchivoUrl;

        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("Documento.NoEncontrado", "No encontramos este documento."));

        if (ConcurrenciaOptimista.Verificar(documento, request.Version, "este documento") is { } conflicto)
            return Result.Fallo(conflicto);

        var tipoDocumento = await dbContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == documento.TipoDocumentoId, cancellationToken);

        if (tipoDocumento is null)
            return Result.Fallo(Error.Crear("Documento.TipoDocumentoNoEncontrado", "No encontramos el tipo de documento asociado."));

        var fechaVencimiento = tipoDocumento.AplicaVencimientoAutomatico
            ? CalculadoraEstadoDocumento.CalcularFechaVencimiento(request.FechaEmision, tipoDocumento.VigenciaMeses)
            : request.FechaVencimientoManual;
        documento.Renovar(request.FechaEmision, fechaVencimiento);

        if (!string.IsNullOrWhiteSpace(request.ArchivoUrl))
        {
            documento.AdjuntarArchivo(request.ArchivoUrl);

            // La mensualidad entra por aquí, no por Crear: el certificado de
            // agosto reemplaza al de julio renovando el mismo Documento. Sin
            // este reencolado, el archivo nuevo jamás se validaría. Mismo
            // SaveChangesAsync que el resto del cambio — se confirman juntos
            // o ninguno, la garantía de CrearDocumentoCommand. Los análisis
            // IA (VerificacionIa/DeteccionTrabajadores) siguen sin reencolarse
            // al renovar — decisión pendiente aparte, por su coste de LLM en
            // cada renovación (plan de la épica, PR-3).
            if (tipoDocumento.PerfilDocumentoOficial != PerfilDocumentoOficial.Ninguno)
            {
                var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
                colaAnalisis.Agregar(new TrabajoAnalisisDocumento(
                    documento.Id, usuarioId, TipoAnalisisDocumento.VerificacionFirmaDigital));
            }
        }

        documento.ActualizarComentarios(request.Comentarios);

        // Invariante de docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (b): la
        // versión anterior del documento ya no es la que hay que validar en
        // ningún portal — renovar reinicia todas sus acreditaciones a
        // Pendiente de subir. El historial de rechazos no se toca (sigue
        // siendo un hecho pasado real). No se re-derivan canales nuevos aquí
        // — si la asignación del trabajador cambió, es un caso fuera de
        // alcance de este lote (Lote 2-D), sin decisión tomada todavía.
        var acreditaciones = await acreditacionRepositorio.ObtenerPorDocumentoIdAsync(documento.Id, cancellationToken);
        foreach (var acreditacion in acreditaciones)
            acreditacion.ReiniciarPorRenovacionDocumento();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // DESPUÉS del commit, nunca antes: si se borrara primero y el commit
        // fallara, el documento seguiría apuntando a un archivo ya destruido.
        // Mismo patrón, y mismas limitaciones, que FirmarDocumentoEnCampoCommand
        // — mejor esfuerzo con constancia. Una caída entre el commit y el
        // borrado deja un huérfano; cerrarlo del todo exige un registro durable
        // de supresiones con reintentos, pendiente de decisión (informe del
        // Módulo 2, compartido con el Módulo 7).
        //
        // Es seguro borrar desde aquí porque una clave de archivo tiene una
        // única fila propietaria: hasta el Módulo 6 (#370),
        // ActualizarDocumentoDesdeAdjuntoCommand reutilizaba la clave del
        // AdjuntoMensaje, y borrar aquí habría destruido el adjunto de la
        // conversación. Ahora copia a una clave propia del Documento.
        if (archivoAnterior is not null && archivoAnterior != documento.ArchivoUrl)
        {
            try
            {
                await almacenamiento.EliminarAsync(archivoAnterior, cancellationToken);
            }
            catch (Exception ex)
            {
                // No se revierte la renovación por esto: ya está confirmada y es
                // correcta. Queda constancia con la clave, que es lo único que
                // permite borrarlo a mano después.
                logger.LogError(ex,
                    "No se pudo borrar el archivo anterior {Archivo} del documento {DocumentoId} tras renovarlo. " +
                    "Queda en almacenamiento sin fila que lo referencie.",
                    archivoAnterior, documento.Id);
            }
        }

        // Renovar puede sacar de "Vencido" el último documento que bloqueaba el
        // expediente de una visita pendiente.
        await publisher.Publish(new DocumentacionCambiadaEvent(documento.Id), cancellationToken);

        return Result.Exito();
    }
}
