using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;

public record ObtenerDocumentoPorIdQuery(Guid Id) : IRequest<DocumentoDetalleDto?>;

/// <summary>
/// <paramref name="Version"/> viaja al formulario para volver en
/// <c>RenovarDocumentoCommand</c>: es lo que permite detectar que otra
/// persona renovó el documento mientras tanto (ver
/// <c>ClienteDetalleDto</c> para el mismo patrón en Cliente).
/// </summary>
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
    string? TipoDocumentoObservaciones,
    Guid Version,
    PerfilDocumentoOficial TipoDocumentoPerfilDocumentoOficial,
    Guid? EmpresaId);

public class ObtenerDocumentoPorIdQueryHandler(IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, IProyectosQueryContext proyectosContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext, IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentoPorIdQuery, DocumentoDetalleDto?>
{
    public async Task<DocumentoDetalleDto?> Handle(ObtenerDocumentoPorIdQuery request, CancellationToken cancellationToken)
    {
        var documento = await documentosContext.Documentos
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
                d.Comentarios,
                d.Version
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (documento is null) return null;

        // El ámbito (Trabajador/Cliente/Vehículo/Empresa) determina contra qué
        // cartera se comprueba el alcance — son cuatro FKs mutuamente
        // excluyentes (ver Fase 29 de ROADMAP.md), así que solo una aplica.
        // Documento de Trabajador es el caso más sensible: incluye archivos de
        // vigilancia de la salud (categoría especial Art. 9 RGPD).
        var proyectoClienteId = documento.ProyectoId is { } proyectoIdVisibilidad
            ? await proyectosContext.Proyectos.Where(p => p.Id == proyectoIdVisibilidad).Select(p => (Guid?)p.ClienteId).FirstOrDefaultAsync(cancellationToken)
            : null;

        // Rama Empresa en alcance de LECTURA es correcta (REC-149, se
        // queda): el documento ES el objeto del portal — es literalmente la
        // documentación de cumplimiento que un Cliente necesita revisar de
        // su contratista. Cambiar esta rama a gestión vaciaría la pestaña
        // "Documentación" para el mismo usuario al que el portal existe
        // para servir.
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

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.Id == documento.TipoDocumentoId)
            .Select(t => new
            {
                t.Nombre,
                t.AplicaVencimientoAutomatico,
                t.Descripcion,
                t.CriteriosValidacion,
                t.SeSolicitaA,
                t.Observaciones,
                t.PerfilDocumentoOficial
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (tipoDocumento is null) return null;

        var (ambito, propietarioNombre) = documento.TrabajadorId is not null
            ? (AmbitoAplicacion.Trabajador, await trabajadoresContext.Trabajadores
                .Where(t => t.Id == documento.TrabajadorId)
                .Select(t => t.Nombre + " " + t.Apellidos)
                .FirstAsync(cancellationToken))
            : documento.ClienteId is not null
                // Documento.ClienteId ya apunta a Empresas (F3).
                ? (AmbitoAplicacion.Cliente, await empresasContext.Empresas
                    .Where(e => e.Id == documento.ClienteId)
                    .Select(e => e.RazonSocial)
                    .FirstAsync(cancellationToken))
                : documento.VehiculoId is not null
                    ? (AmbitoAplicacion.Vehiculo, await vehiculosContext.Vehiculos
                        .Where(v => v.Id == documento.VehiculoId)
                        .Select(v => v.Nombre + " (" + v.NumeroPlaca + ")")
                        .FirstAsync(cancellationToken))
                    : documento.ProyectoId is not null
                        ? (AmbitoAplicacion.Proyecto, await proyectosContext.Proyectos
                            .Where(p => p.Id == documento.ProyectoId)
                            .Select(p => p.Nombre)
                            .FirstAsync(cancellationToken))
                        : (AmbitoAplicacion.Empresa, await empresasContext.Empresas
                            .Where(e => e.Id == documento.EmpresaId)
                            .Select(e => e.RazonSocial)
                            .FirstAsync(cancellationToken));

        return new DocumentoDetalleDto(
            documento.Id, ambito, propietarioNombre, tipoDocumento.Nombre,
            tipoDocumento.AplicaVencimientoAutomatico, documento.FechaEmision, documento.FechaVencimiento,
            documento.ArchivoUrl, documento.Comentarios,
            tipoDocumento.Descripcion, tipoDocumento.CriteriosValidacion, tipoDocumento.SeSolicitaA, tipoDocumento.Observaciones,
            documento.Version, tipoDocumento.PerfilDocumentoOficial, documento.EmpresaId);
    }
}
