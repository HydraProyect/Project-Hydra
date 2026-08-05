using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;

/// <summary>
/// Fase D ("Aceptar lo detectado por la IA"): a diferencia de
/// <see cref="Documentos.Commands.ResolverRevisionIaDocumento.ResolverRevisionIaDocumentoCommand"/>
/// (que deliberadamente nunca toca el Documento — ver Issue #19), este
/// Command sí lo renueva con lo que detectó la IA, para el caso contrario:
/// el gestor revisa el aviso y está de acuerdo con lo que la IA leyó, así
/// que aceptarlo no debería obligar a abrir el Drawer de Documento y
/// escribir la misma fecha a mano.
///
/// Reutiliza exactamente la regla de <c>RenovarDocumentoCommandHandler</c>
/// para el vencimiento: si el TipoDocumento calcula la vigencia
/// automáticamente, se recalcula desde la fecha de emisión detectada (la
/// fecha de vencimiento es siempre un cálculo, nunca un dato de entrada —
/// ver DATABASE.md); solo si no aplica cálculo automático se usa la fecha de
/// vencimiento que detectó la IA.
/// </summary>
public record AplicarDeteccionIaDocumentoCommand(Guid RevisionId) : ICommand;

public class AplicarDeteccionIaDocumentoCommandHandler(
    IRevisionIaDocumentoRepository revisionRepositorio,
    IDocumentoRepository documentoRepositorio,
    IAprobacionDocumentoRepository aprobacionRepositorio,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos,
    IProyectosQueryContext proyectosContext,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<AplicarDeteccionIaDocumentoCommand, Result>
{
    public async Task<Result> Handle(AplicarDeteccionIaDocumentoCommand request, CancellationToken cancellationToken)
    {
        var revision = await revisionRepositorio.ObtenerPorIdAsync(request.RevisionId, cancellationToken);
        if (revision is null)
            return Result.Fallo(Error.Crear("RevisionIa.NoEncontrada", "No encontramos esta revisión."));

        if (revision.Resuelta)
            return Result.Fallo(Error.Crear("RevisionIa.YaResuelta", "Esta revisión ya fue gestionada."));

        if (revision.FechaEmisionDetectada is null)
            return Result.Fallo(Error.Crear(
                "RevisionIa.SinFechaDetectada", "La IA no detectó una fecha de emisión que aplicar — corrige el documento a mano."));

        var documento = await documentoRepositorio.ObtenerPorIdAsync(revision.DocumentoId, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("RevisionIa.NoEncontrada", "No encontramos esta revisión."));

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == documento.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Result.Fallo(Error.Crear("Documento.TipoDocumentoNoEncontrado", "No encontramos el tipo de documento asociado."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("RevisionIa.SinUsuario", "No pudimos identificar quién acepta esta revisión."));

        var fechaEmision = revision.FechaEmisionDetectada.Value;
        var fechaVencimiento = tipoDocumento.AplicaVencimientoAutomatico
            ? CalculadoraEstadoDocumento.CalcularFechaVencimiento(fechaEmision, tipoDocumento.VigenciaMeses)
            : revision.FechaVencimientoDetectada;

        documento.Renovar(fechaEmision, fechaVencimiento);
        revision.Resolver();
        aprobacionRepositorio.Agregar(AprobacionDocumento.CrearManual(revision.DocumentoId, revision.ConfianzaGeneral, usuarioId.Value));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
