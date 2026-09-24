using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.CorregirRevisionIaDocumento;

/// <summary>
/// Corrige manualmente la fecha de emisión de un Documento y resuelve su
/// revisión IA en el mismo <see cref="IUnitOfWork"/>. No modifica el archivo ni
/// convierte la firma detectada en una firma: esos datos no los produce este
/// flujo. Usa la misma autorización por <see cref="ICommand"/> que
/// AplicarDeteccionIaDocumentoCommand.
/// </summary>
public record CorregirRevisionIaDocumentoCommand(Guid RevisionId, DateOnly FechaEmision) : ICommand;

public class CorregirRevisionIaDocumentoCommandHandler(
    IRevisionIaDocumentoRepository revisionRepositorio,
    IDocumentoRepository documentoRepositorio,
    IAprobacionDocumentoRepository aprobacionRepositorio,
    IAuditoriaExtraccionIaRepository auditoriaRepositorio,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos,
    IProyectosQueryContext proyectosContext,
    ICurrentUserService currentUserService,
    IPublisher publisher,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CorregirRevisionIaDocumentoCommand, Result>
{
    public async Task<Result> Handle(CorregirRevisionIaDocumentoCommand request, CancellationToken cancellationToken)
    {
        var revision = await revisionRepositorio.ObtenerPorIdAsync(request.RevisionId, cancellationToken);
        if (revision is null)
            return Result.Fallo(Error.Crear("RevisionIa.NoEncontrada", "No encontramos esta revisión."));

        if (revision.Resuelta)
            return Result.Fallo(Error.Crear("RevisionIa.YaResuelta", "Esta revisión ya fue gestionada."));

        var documento = await documentoRepositorio.ObtenerPorIdAsync(revision.DocumentoId, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("RevisionIa.NoEncontrada", "No encontramos esta revisión."));

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == documento.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Result.Fallo(Error.Crear("Documento.TipoDocumentoNoEncontrado", "No encontramos el tipo de documento asociado."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("RevisionIa.SinUsuario", "No pudimos identificar quién corrige esta revisión."));

        // Solo se corrige la emisión: si el tipo no vence automáticamente, la
        // vigencia que ya tenía el documento —fecha, «no caduca» o sin
        // confirmar— se conserva tal cual.
        var vigencia = tipoDocumento.AplicaVencimientoAutomatico
            ? VigenciaDocumento.DesdeFechaOpcional(
                CalculadoraEstadoDocumento.CalcularFechaVencimiento(request.FechaEmision, tipoDocumento.VigenciaMeses))
            : documento.Vigencia;

        documento.Renovar(request.FechaEmision, vigencia);
        revision.Resolver();
        aprobacionRepositorio.Agregar(AprobacionDocumento.CrearManual(revision.DocumentoId, revision.ConfianzaGeneral, usuarioId.Value));

        // Las revisiones históricas sin vínculo no eligen una auditoría por
        // fecha: sería atribuirles otra extracción del mismo documento.
        var auditoria = revision.AuditoriaExtraccionIaId is { } auditoriaId
            ? await auditoriaRepositorio.ObtenerPorIdAsync(auditoriaId, cancellationToken)
            : null;
        auditoria?.RegistrarDecisionHumana(DecisionHumanaIa.DescartadaManual, usuarioId.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await publisher.Publish(new DocumentacionCambiadaEvent(revision.DocumentoId), cancellationToken);

        return Result.Exito();
    }
}
