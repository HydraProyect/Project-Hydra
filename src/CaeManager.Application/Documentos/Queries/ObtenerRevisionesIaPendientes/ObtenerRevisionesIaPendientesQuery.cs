using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;

/// <summary>Revisiones IA pendientes (sin resolver) de Documentos de Trabajador, acotadas al alcance de cartera del usuario actual — ver VerificacionIaDocumentoService.</summary>
public record ObtenerRevisionesIaPendientesQuery : IRequest<IReadOnlyList<RevisionIaDocumentoDto>>;

public record RevisionIaDocumentoDto(
    Guid Id,
    Guid DocumentoId,
    string TrabajadorNombre,
    string TipoDocumentoNombre,
    int ConfianzaGeneral,
    string? TipoDetectado,
    DateOnly? FechaEmisionDetectada,
    string Motivo,
    DateTime CreadaEnUtc);

public class ObtenerRevisionesIaPendientesQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerRevisionesIaPendientesQuery, IReadOnlyList<RevisionIaDocumentoDto>>
{
    public async Task<IReadOnlyList<RevisionIaDocumentoDto>> Handle(
        ObtenerRevisionesIaPendientesQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        var consulta =
            from revision in dbContext.RevisionesIaDocumento
            where !revision.Resuelta
            join documento in dbContext.Documentos on revision.DocumentoId equals documento.Id
            join trabajador in dbContext.Trabajadores on documento.TrabajadorId equals trabajador.Id
            join tipoDocumento in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(trabajador.Id)
            orderby revision.CreadaEnUtc descending
            select new RevisionIaDocumentoDto(
                revision.Id, revision.DocumentoId, trabajador.Nombre + " " + trabajador.Apellidos, tipoDocumento.Nombre,
                revision.ConfianzaGeneral, revision.TipoDetectado, revision.FechaEmisionDetectada, revision.Motivo, revision.CreadaEnUtc);

        return await consulta.ToListAsync(cancellationToken);
    }
}
