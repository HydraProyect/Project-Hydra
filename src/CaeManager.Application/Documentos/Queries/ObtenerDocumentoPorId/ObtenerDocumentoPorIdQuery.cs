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
    EstadoVigenciaDocumento EstadoVigencia,
    string? ArchivoUrl,
    string? Comentarios,
    string? TipoDocumentoDescripcion,
    string? TipoDocumentoCriteriosValidacion,
    string? TipoDocumentoSeSolicitaA,
    string? TipoDocumentoObservaciones,
    Guid Version,
    PerfilDocumentoOficial TipoDocumentoPerfilDocumentoOficial,
    Guid? EmpresaId,
    // Titular de un documento de Trabajador: Documento 360 abre con él la reclamación
    // existente (DrawerReclamacionLote). Opcional y al final: ningún productor cambia.
    Guid? TrabajadorId = null,
    // Nombre canónico para descargar/enviar/exportar (NombreArchivoDocumento.Suelto):
    // calculado aquí, donde se conocen propietario, tipo y coincidencias del mismo día.
    string NombreArchivoDescarga = "",
    // Historial (D8 del diseño del documento efectivo): un enlace a un Id que otro documento sustituyó resuelve aquí,
    // con el Id del que lo sustituyó, para que la pantalla diga «sustituido por…» y lleve al vigente. Nulos si el
    // documento sigue en uso. Opcionales y al final: ningún productor cambia.
    Guid? SustitutoId = null,
    DateTime? SustituidoEn = null);

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
                d.EstadoVigencia,
                d.ArchivoUrl,
                d.Comentarios,
                d.Version,
                d.CreadoEnUtc,
                d.SustituidoPorDocumentoId,
                d.SustituidoEnUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (documento is null) return null;

        // El ámbito (Trabajador/Cliente/Vehículo/Empresa) determina contra qué
        // cartera se comprueba el alcance — son cuatro FKs mutuamente
        // excluyentes (ver Fase 29 de Project-Hydra-Negocio/tecnico/ROADMAP.md), así que solo una aplica.
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

        var nombreEnArchivo = documento.TrabajadorId is not null
            ? await trabajadoresContext.Trabajadores
                .Where(t => t.Id == documento.TrabajadorId)
                .Select(t => t.Apellidos + " " + t.Nombre)
                .FirstAsync(cancellationToken)
            : propietarioNombre;

        // Coincidencias de mismo propietario, tipo y emisión: la más antigua conserva el
        // nombre sin sufijo y las siguientes llevan _v2, _v3… de forma estable.
        // "Mismo tipo" = mismo tipo canónico de nombre de fichero (Aptitud médica y Reconocimiento
        // médico dan el mismo nombre aunque sean TipoDocumentoId distintos).
        var tiposDeAnteriores = await documentosContext.Documentos
            .Where(d => d.Id != documento.Id
                && d.FechaEmision == documento.FechaEmision
                && d.TrabajadorId == documento.TrabajadorId
                && d.ClienteId == documento.ClienteId
                && d.EmpresaId == documento.EmpresaId
                && d.VehiculoId == documento.VehiculoId
                && d.ProyectoId == documento.ProyectoId
                && (d.CreadoEnUtc < documento.CreadoEnUtc
                    || (d.CreadoEnUtc == documento.CreadoEnUtc && d.Id.CompareTo(documento.Id) < 0)))
            .Select(d => d.TipoDocumentoId)
            .ToListAsync(cancellationToken);
        var nombresDeTipos = tiposDeAnteriores.Count == 0
            ? new Dictionary<Guid, string>()
            : await tiposDocumentoContext.TiposDocumento
                .Where(t => tiposDeAnteriores.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);
        var anteriores = tiposDeAnteriores.Count(id =>
            nombresDeTipos.TryGetValue(id, out var nombre) && NombreArchivoDocumento.MismoTipo(nombre, tipoDocumento.Nombre));

        return new DocumentoDetalleDto(
            documento.Id, ambito, propietarioNombre, tipoDocumento.Nombre,
            tipoDocumento.AplicaVencimientoAutomatico, documento.FechaEmision, documento.FechaVencimiento,
            documento.EstadoVigencia, documento.ArchivoUrl, documento.Comentarios,
            tipoDocumento.Descripcion, tipoDocumento.CriteriosValidacion, tipoDocumento.SeSolicitaA, tipoDocumento.Observaciones,
            documento.Version, tipoDocumento.PerfilDocumentoOficial, documento.EmpresaId, documento.TrabajadorId,
            NombreArchivoDocumento.Suelto(nombreEnArchivo, tipoDocumento.Nombre, documento.FechaEmision, anteriores + 1),
            documento.SustituidoPorDocumentoId, documento.SustituidoEnUtc);
    }
}
