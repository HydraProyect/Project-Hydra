using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerFirmasEnCampoDocumento;

/// <summary>
/// Firmas en campo ya estampadas sobre el archivo de un Documento, para mostrarlas en la pestaña "Firma". Lista vacía
/// si quien pide no puede leer el Documento: fuera de su Asignación de Cartera es igual que no tener firmas (SEG-16).
/// </summary>
public record ObtenerFirmasEnCampoDocumentoQuery(Guid DocumentoId) : IRequest<IReadOnlyList<FirmaEnCampoDocumentoDto>>;

public record FirmaEnCampoDocumentoDto(
    Guid FirmanteUsuarioId,
    string FirmanteNombre,
    string FirmanteRol,
    DateTime FirmadoEnUtc,
    string? Ubicacion,
    string HashSha256Pdf);

public class ObtenerFirmasEnCampoDocumentoQueryHandler(
    IDocumentosQueryContext documentosContext, IProyectosQueryContext proyectosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerFirmasEnCampoDocumentoQuery, IReadOnlyList<FirmaEnCampoDocumentoDto>>
{
    public async Task<IReadOnlyList<FirmaEnCampoDocumentoDto>> Handle(
        ObtenerFirmasEnCampoDocumentoQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.DocumentoLegibleAsync(request.DocumentoId, documentosContext, proyectosContext, cancellationToken))
            return [];

        return await documentosContext.FirmasEnCampoDocumento
            .Where(f => f.DocumentoId == request.DocumentoId)
            .OrderBy(f => f.FirmadoEnUtc)
            .Select(f => new FirmaEnCampoDocumentoDto(
                f.FirmanteUsuarioId, f.FirmanteNombre, f.FirmanteRol, f.FirmadoEnUtc, f.Ubicacion, f.HashSha256Pdf))
            .ToListAsync(cancellationToken);
    }
}
