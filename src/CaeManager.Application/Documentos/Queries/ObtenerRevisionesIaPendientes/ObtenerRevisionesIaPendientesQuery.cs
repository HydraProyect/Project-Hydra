using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
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

public class ObtenerRevisionesIaPendientesQueryHandler(IDocumentosQueryContext documentosContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerRevisionesIaPendientesQuery, IReadOnlyList<RevisionIaDocumentoDto>>
{
    public async Task<IReadOnlyList<RevisionIaDocumentoDto>> Handle(
        ObtenerRevisionesIaPendientesQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        var consulta =
            from revision in documentosContext.RevisionesIaDocumento
            where !revision.Resuelta
            join documento in documentosContext.Documentos on revision.DocumentoId equals documento.Id
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(trabajador.Id)
            orderby revision.CreadaEnUtc descending
            select new RevisionIaDocumentoDto(
                revision.Id, revision.DocumentoId, trabajador.Nombre + " " + trabajador.Apellidos, tipoDocumento.Nombre,
                revision.ConfianzaGeneral, revision.TipoDetectado, revision.FechaEmisionDetectada, revision.Motivo, revision.CreadaEnUtc);

        return await consulta.ToListAsync(cancellationToken);
    }
}
