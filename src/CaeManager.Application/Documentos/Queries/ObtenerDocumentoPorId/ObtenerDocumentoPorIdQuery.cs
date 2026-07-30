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

public class ObtenerDocumentoPorIdQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentoPorIdQuery, DocumentoDetalleDto?>
{
    public async Task<DocumentoDetalleDto?> Handle(ObtenerDocumentoPorIdQuery request, CancellationToken cancellationToken)
    {
        var documento = await dbContext.Documentos
            .Where(d => d.Id == request.Id)
            .Select(d => new
            {
                d.Id,
                d.TrabajadorId,
                d.ClienteId,
                d.EmpresaId,
                d.VehiculoId,
                d.ProyectoId,
                d.TipoDocumentoId,
                d.FechaEmision,
                d.FechaVencimiento,
                d.ArchivoUrl,
                d.Comentarios
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (documento is null) return null;

        // El ámbito (Trabajador/Cliente/Vehículo/Empresa) determina contra qué
        // cartera se comprueba el alcance — son cuatro FKs mutuamente
        // excluyentes (ver Fase 29 de ROADMAP.md), así que solo una aplica.
        // Documento de Trabajador es el caso más sensible: incluye archivos de
        // vigilancia de la salud (categoría especial Art. 9 RGPD).
        var proyectoClienteId = documento.ProyectoId is { } proyectoIdVisibilidad
            ? await dbContext.Proyectos.Where(p => p.Id == proyectoIdVisibilidad).Select(p => (Guid?)p.ClienteId).FirstOrDefaultAsync(cancellationToken)
            : null;

        var visible = documento.TrabajadorId is { } trabajadorId
            ? await alcanceDatos.TrabajadorVisibleAsync(trabajadorId, cancellationToken)
            : documento.ClienteId is { } clienteId
                ? await alcanceDatos.ClienteVisibleAsync(clienteId, cancellationToken)
                : documento.VehiculoId is { } vehiculoId
                    ? await alcanceDatos.VehiculoVisibleAsync(vehiculoId, cancellationToken)
                    : proyectoClienteId is { } clienteIdDeProyecto
                        ? await alcanceDatos.ClienteVisibleAsync(clienteIdDeProyecto, cancellationToken)
                        : await alcanceDatos.EmpresaVisibleAsync(documento.EmpresaId!.Value, cancellationToken);

        if (!visible) return null;

        var tipoDocumento = await dbContext.TiposDocumento
            .Where(t => t.Id == documento.TipoDocumentoId)
            .Select(t => new
            {
                t.Nombre,
                t.AplicaVencimientoAutomatico,
                t.Descripcion,
                t.CriteriosValidacion,
                t.SeSolicitaA,
                t.Observaciones
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
                    : documento.ProyectoId is not null
                        ? (AmbitoAplicacion.Proyecto, await dbContext.Proyectos
                            .Where(p => p.Id == documento.ProyectoId)
                            .Select(p => p.Nombre)
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
