using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;

public record ObtenerDocumentoPorIdQuery(Guid Id) : IRequest<DocumentoDetalleDto?>;

public record DocumentoDetalleDto(
    Guid Id,
    AmbitoAplicacion Ambito,
    string PropietarioNombre,
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
    public async Task<DocumentoDetalleDto?> Handle(ObtenerDocumentoPorIdQuery request, CancellationToken cancellationToken)
    {
        var documento = await dbContext.Documentos
            .Where(d => d.Id == request.Id)
            .Select(d => new
            {
                d.Id, d.TrabajadorId, d.ClienteId, d.EmpresaId, d.VehiculoId, d.TipoDocumentoId,
                d.FechaEmision, d.FechaVencimiento, d.ArchivoUrl, d.Comentarios
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (documento is null) return null;

        var tipoDocumento = await dbContext.TiposDocumento
            .Where(t => t.Id == documento.TipoDocumentoId)
            .Select(t => new
            {
                t.Nombre, t.AplicaVencimientoAutomatico, t.Descripcion, t.CriteriosValidacion, t.SeSolicitaA, t.Observaciones
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (tipoDocumento is null) return null;

        var (ambito, propietarioNombre) = documento.TrabajadorId is not null
            ? (AmbitoAplicacion.Trabajador, await dbContext.Trabajadores
                .Where(t => t.Id == documento.TrabajadorId)
                .Select(t => t.Nombre + " " + t.Apellidos)
                .FirstAsync(cancellationToken))
            : documento.ClienteId is not null
                ? (AmbitoAplicacion.Cliente, await dbContext.Clientes
                    .Where(c => c.Id == documento.ClienteId)
                    .Select(c => c.RazonSocial)
                    .FirstAsync(cancellationToken))
                : documento.VehiculoId is not null
                    ? (AmbitoAplicacion.Vehiculo, await dbContext.Vehiculos
                        .Where(v => v.Id == documento.VehiculoId)
                        .Select(v => v.Nombre + " (" + v.NumeroPlaca + ")")
                        .FirstAsync(cancellationToken))
                    : (AmbitoAplicacion.Empresa, await dbContext.Empresas
                        .Where(e => e.Id == documento.EmpresaId)
                        .Select(e => e.RazonSocial)
                        .FirstAsync(cancellationToken));

        return new DocumentoDetalleDto(
            documento.Id, ambito, propietarioNombre, tipoDocumento.Nombre,
            tipoDocumento.AplicaVencimientoAutomatico, documento.FechaEmision, documento.FechaVencimiento,
            documento.ArchivoUrl, documento.Comentarios,
            tipoDocumento.Descripcion, tipoDocumento.CriteriosValidacion, tipoDocumento.SeSolicitaA, tipoDocumento.Observaciones);
    }
}
