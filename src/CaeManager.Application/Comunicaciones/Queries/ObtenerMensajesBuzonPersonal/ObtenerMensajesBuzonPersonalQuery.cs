using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerMensajesBuzonPersonal;

/// <summary>
/// Lista plana de mensajes entrantes del buzón personal del gestor actual (ronda de reducción de
/// ruido en Comunicaciones, patrón "buzón personal") — nunca mezclada con conversaciones de
/// Cliente, ni siquiera las de la cola de triage. Usa <see cref="ICurrentUserService"/> para
/// resolver "el mío" — no recibe el gestor como parámetro, mismo criterio que
/// <c>ObtenerConversacionesQuery.SoloAsignadasAMi</c>, para que no pueda usarse para leer el buzón
/// personal de otro gestor.
/// </summary>
public record ObtenerMensajesBuzonPersonalQuery : IRequest<IReadOnlyList<MensajeBuzonPersonalDto>>;

public record MensajeBuzonPersonalDto(
    Guid MensajeId, Guid ConversacionId, string Asunto, string Remitente, string CuerpoHtml, DateTime FechaUtc,
    bool EsNotificacionAutomatica, string? ProveedorPlataformaCaeNombre, MotivoRuidoMensaje Motivo);

public class ObtenerMensajesBuzonPersonalQueryHandler(
    IIntegracionesQueryContext integracionesContext, IComunicacionesQueryContext comunicacionesContext,
    IProveedoresPlataformaCaeQueryContext proveedoresContext, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerMensajesBuzonPersonalQuery, IReadOnlyList<MensajeBuzonPersonalDto>>
{
    public async Task<IReadOnlyList<MensajeBuzonPersonalDto>> Handle(
        ObtenerMensajesBuzonPersonalQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();

        var conexionIds = await integracionesContext.ConexionesIntegracion
            .Where(c => c.GestorPropietarioId == usuarioId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (conexionIds.Count == 0) return [];

        var conversaciones = await comunicacionesContext.Conversaciones
            .Where(c => c.ConexionIntegracionId != null && conexionIds.Contains(c.ConexionIntegracionId!.Value))
            .Select(c => new { c.Id, c.Asunto })
            .ToListAsync(cancellationToken);

        if (conversaciones.Count == 0) return [];

        var conversacionIds = conversaciones.Select(c => c.Id).ToList();
        var asuntoPorConversacion = conversaciones.ToDictionary(c => c.Id, c => c.Asunto);

        var mensajes = await comunicacionesContext.Mensajes
            .Where(m => conversacionIds.Contains(m.ConversacionId) && m.Direccion == DireccionMensaje.Entrante)
            .Select(m => new { m.Id, m.ConversacionId, m.Remitente, m.CuerpoHtml, m.FechaUtc })
            .ToListAsync(cancellationToken);

        if (mensajes.Count == 0) return [];

        var mensajeIds = mensajes.Select(m => m.Id).ToList();

        var clasificaciones = await comunicacionesContext.ClasificacionesRuidoMensaje
            .Where(c => mensajeIds.Contains(c.MensajeId))
            .ToDictionaryAsync(c => c.MensajeId, cancellationToken);

        var proveedorIds = clasificaciones.Values
            .Where(c => c.ProveedorPlataformaCaeId is not null)
            .Select(c => c.ProveedorPlataformaCaeId!.Value)
            .Distinct()
            .ToList();

        var nombresProveedor = proveedorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await proveedoresContext.ProveedoresPlataformaCae
                .Where(p => proveedorIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Nombre, cancellationToken);

        return mensajes
            .OrderByDescending(m => m.FechaUtc)
            .Select(m =>
            {
                var clasificacion = clasificaciones.GetValueOrDefault(m.Id);
                var proveedorNombre = clasificacion?.ProveedorPlataformaCaeId is { } proveedorId
                    ? nombresProveedor.GetValueOrDefault(proveedorId)
                    : null;

                return new MensajeBuzonPersonalDto(
                    m.Id, m.ConversacionId, asuntoPorConversacion.GetValueOrDefault(m.ConversacionId, string.Empty),
                    m.Remitente, m.CuerpoHtml, m.FechaUtc,
                    clasificacion?.EsNotificacionAutomatica ?? false, proveedorNombre,
                    clasificacion?.Motivo ?? MotivoRuidoMensaje.Ninguno);
            })
            .ToList();
    }
}
