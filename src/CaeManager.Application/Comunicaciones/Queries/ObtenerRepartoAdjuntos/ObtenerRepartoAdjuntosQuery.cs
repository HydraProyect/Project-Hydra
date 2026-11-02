using CaeManager.Application.Common;
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
/// </summary>
public class ObtenerRepartoAdjuntosQueryHandler(
    IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos, IPoliticaNotaInterna politicaNotaInterna)
    : IRequestHandler<ObtenerRepartoAdjuntosQuery, IReadOnlyList<AdjuntoRepartoDto>>
{
    public async Task<IReadOnlyList<AdjuntoRepartoDto>> Handle(ObtenerRepartoAdjuntosQuery request, CancellationToken cancellationToken)
    {
        if (!await politicaNotaInterna.PuedeLeerAsync(cancellationToken)
            || !await RepartoAdjuntos.ConversacionVisibleAsync(comunicacionesContext, alcanceDatos, request.ConversacionId, cancellationToken))
            return [];

        return await (
            from a in comunicacionesContext.AdjuntosMensaje
            join m in comunicacionesContext.Mensajes on a.MensajeId equals m.Id
            where m.ConversacionId == request.ConversacionId && m.Direccion == DireccionMensaje.Entrante
            orderby m.FechaUtc, a.NombreArchivo
            select new AdjuntoRepartoDto(
                a.Id, a.MensajeId, a.NombreArchivo, a.TipoContenido, a.TamanoBytes, a.EstadoReparto,
                a.DestinoTrabajadorId, a.DestinoEmpresaId, a.TipoDocumentoPropuestoId, a.MotivoRechazo,
                a.ResueltoPorUsuarioId, a.ResueltoEnUtc, a.DocumentoId))
            .ToListAsync(cancellationToken);
    }
}
