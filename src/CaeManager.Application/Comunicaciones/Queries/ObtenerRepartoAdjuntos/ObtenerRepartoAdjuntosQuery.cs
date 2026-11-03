using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerRepartoAdjuntos;

public record AdjuntoRepartoDto(
    Guid Id, Guid MensajeId, string NombreArchivo, string TipoContenido, long TamanoBytes, EstadoRepartoAdjunto Estado,
    Guid? DestinoTrabajadorId, Guid? DestinoEmpresaId, Guid? TipoDocumentoId, string? MotivoRechazo,
    Guid? ResueltoPorUsuarioId, DateTime? ResueltoEnUtc, Guid? DocumentoId);

public record ObtenerRepartoAdjuntosQuery(Guid ConversacionId) : IRequest<IReadOnlyList<AdjuntoRepartoDto>>;

/// <summary>
/// El reparto de los adjuntos recibidos en un hilo. Es trabajo interno del Tenant: el destino y el motivo de rechazo
/// no llegan al rol Cliente de portal, por eso la lectura pasa por <see cref="IPoliticaNotaInterna"/> además del
/// alcance. Sin permiso o sin hilo visible devuelve una lista vacía.
///
/// Ver el hilo no da acceso a lo que cuelga de él: otra Asignación de Cartera pudo fijar un destino o asignar un
/// Documento que esta sesión no alcanza, y esos Ids salen vacíos. La fila se queda, porque el adjunto es del hilo. Las
/// listas de alcance se resuelven una vez por ámbito, así que comprobar fila a fila no consulta por fila; los Documentos
/// se leen de una vez.
/// </summary>
public class ObtenerRepartoAdjuntosQueryHandler(
    IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos, IPoliticaNotaInterna politicaNotaInterna,
    IDocumentosQueryContext documentosContext, IProyectosQueryContext proyectosContext)
    : IRequestHandler<ObtenerRepartoAdjuntosQuery, IReadOnlyList<AdjuntoRepartoDto>>
{
    public async Task<IReadOnlyList<AdjuntoRepartoDto>> Handle(ObtenerRepartoAdjuntosQuery request, CancellationToken cancellationToken)
    {
        if (!await politicaNotaInterna.PuedeLeerAsync(cancellationToken)
            || !await RepartoAdjuntos.ConversacionVisibleAsync(comunicacionesContext, alcanceDatos, request.ConversacionId, cancellationToken))
            return [];

        var filas = await (
            from a in comunicacionesContext.AdjuntosMensaje
            join m in comunicacionesContext.Mensajes on a.MensajeId equals m.Id
            where m.ConversacionId == request.ConversacionId && m.Direccion == DireccionMensaje.Entrante
            orderby m.FechaUtc, a.NombreArchivo, a.Id
            select new AdjuntoRepartoDto(
                a.Id, a.MensajeId, a.NombreArchivo, a.TipoContenido, a.TamanoBytes, a.EstadoReparto,
                a.DestinoTrabajadorId, a.DestinoEmpresaId, a.TipoDocumentoPropuestoId, a.MotivoRechazo,
                a.ResueltoPorUsuarioId, a.ResueltoEnUtc, a.DocumentoId))
            .ToListAsync(cancellationToken);

        var documentosLegibles = await alcanceDatos.DocumentosLegiblesAsync(
            filas.Where(f => f.DocumentoId is not null).Select(f => f.DocumentoId!.Value).Distinct().ToList(),
            documentosContext, proyectosContext, cancellationToken);

        var resultado = new List<AdjuntoRepartoDto>(filas.Count);
        foreach (var fila in filas)
        {
            resultado.Add(fila with
            {
                DestinoTrabajadorId = fila.DestinoTrabajadorId is { } trabajadorId && await alcanceDatos.TrabajadorVisibleAsync(trabajadorId, cancellationToken)
                    ? fila.DestinoTrabajadorId : null,
                DestinoEmpresaId = fila.DestinoEmpresaId is { } empresaId && await alcanceDatos.EmpresaParaGestionVisibleAsync(empresaId, cancellationToken)
                    ? fila.DestinoEmpresaId : null,
                DocumentoId = fila.DocumentoId is { } documentoId && documentosLegibles.Contains(documentoId) ? fila.DocumentoId : null
            });
        }

        return resultado;
    }
}
