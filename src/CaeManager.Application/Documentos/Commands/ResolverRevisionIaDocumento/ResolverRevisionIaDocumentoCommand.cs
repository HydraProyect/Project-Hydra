using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.ResolverRevisionIaDocumento;

/// <summary>
/// Marca una RevisionIaDocumento como revisada — nunca modifica el
/// Documento en sí (ver Issue #19: la corrección, si hace falta, la hace un
/// Gestor CAE editando el Documento por la vía normal, no este comando).
/// </summary>
public record ResolverRevisionIaDocumentoCommand(Guid RevisionId) : ICommand;

public class ResolverRevisionIaDocumentoCommandHandler(
    IRevisionIaDocumentoRepository revisionRepositorio,
    IAprobacionDocumentoRepository aprobacionRepositorio,
    IDocumentosQueryContext dbContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ResolverRevisionIaDocumentoCommand, Result>
{
    public async Task<Result> Handle(ResolverRevisionIaDocumentoCommand request, CancellationToken cancellationToken)
    {
        var revision = await revisionRepositorio.ObtenerPorIdAsync(request.RevisionId, cancellationToken);
        if (revision is null)
            return Result.Fallo(Error.Crear("RevisionIa.NoEncontrada", "No encontramos esta revisión."));

        if (revision.Resuelta)
            return Result.Fallo(Error.Crear("RevisionIa.YaResuelta", "Esta revisión ya fue gestionada."));

        var propietario = await dbContext.Documentos
            .Where(d => d.Id == revision.DocumentoId)
            .Select(d => new { d.TrabajadorId, d.EmpresaId })
            .FirstOrDefaultAsync(cancellationToken);

        // Mismo alcance que la bandeja (ObtenerRevisionesIaPendientesQuery):
        // documento de Trabajador ⇒ cartera; documento de Empresa (validación
        // oficial) ⇒ solo roles de visión total.
        var visible = propietario?.TrabajadorId is { } trabajadorId
            ? await alcanceDatos.TrabajadorVisibleAsync(trabajadorId, cancellationToken)
            : propietario?.EmpresaId is not null
                && await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken) is null;

        if (!visible)
            return Result.Fallo(Error.Crear("RevisionIa.NoEncontrada", "No encontramos esta revisión."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("RevisionIa.SinUsuario", "No pudimos identificar quién resuelve esta revisión."));

        revision.Resolver();
        aprobacionRepositorio.Agregar(AprobacionDocumento.CrearManual(revision.DocumentoId, revision.ConfianzaGeneral, usuarioId.Value));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
