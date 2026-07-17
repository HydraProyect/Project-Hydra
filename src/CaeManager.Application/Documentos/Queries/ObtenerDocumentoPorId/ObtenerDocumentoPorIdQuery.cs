using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;

public record ObtenerDocumentoPorIdQuery(Guid Id) : IRequest<DocumentoDetalleDto?>;

public record DocumentoDetalleDto(
    Guid Id,
    string TrabajadorNombre,
    string TipoDocumentoNombre,
    bool TipoDocumentoAplicaVencimientoAutomatico,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    string? ArchivoUrl,
    string? Comentarios,
    string? TipoDocumentoDescripcion,
    string? TipoDocumentoCriteriosValidacion,
    string? TipoDocumentoSeSolicitaA,
    string? TipoDocumentoObservaciones);

public class ObtenerDocumentoPorIdQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerDocumentoPorIdQuery, DocumentoDetalleDto?>
{
    public Task<DocumentoDetalleDto?> Handle(ObtenerDocumentoPorIdQuery request, CancellationToken cancellationToken) =>
        (from documento in dbContext.Documentos
         join trabajador in dbContext.Trabajadores on documento.TrabajadorId equals trabajador.Id
         join tipoDocumento in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
         where documento.Id == request.Id
         select new DocumentoDetalleDto(
             documento.Id, trabajador.Nombre + " " + trabajador.Apellidos, tipoDocumento.Nombre,
             tipoDocumento.AplicaVencimientoAutomatico, documento.FechaEmision, documento.FechaVencimiento,
             documento.ArchivoUrl, documento.Comentarios,
             tipoDocumento.Descripcion, tipoDocumento.CriteriosValidacion, tipoDocumento.SeSolicitaA, tipoDocumento.Observaciones))
        .FirstOrDefaultAsync(cancellationToken);
}
