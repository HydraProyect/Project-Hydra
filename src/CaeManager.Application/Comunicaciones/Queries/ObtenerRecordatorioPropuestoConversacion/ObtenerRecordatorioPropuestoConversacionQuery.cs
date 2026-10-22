using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerRecordatorioPropuestoConversacion;

public record ObtenerRecordatorioPropuestoConversacionQuery(Guid ConversacionId) : IRequest<RecordatorioPropuestoDto?>;

/// <param name="Destinatario">
/// A quién sale si se aprueba: el remitente del último mensaje entrante con id externo, el mismo al que responde
/// <see cref="Commands.ResponderConversacion.ResponderConversacionCommandHandler"/>. Nulo si no hay ninguno.
/// </param>
public record RecordatorioPropuestoDto(Guid PropuestaId, DateTime EsperaDesdeUtc, DateTime PropuestaEnUtc, string? Destinatario = null);

/// <summary>
/// La propuesta de recordatorio Pendiente de una conversación, si sigue vigente (ver
/// <see cref="PropuestaRecordatorioConversacion"/>). Una propuesta cuya espera ya terminó no se devuelve aunque el proceso
/// de fondo todavía no la haya descartado: el Gestor CAE nunca ve un recordatorio que ya no toca.
/// Mismo alcance que el propio hilo (<see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/>); fuera de
/// alcance devuelve <c>null</c>, igual que sin propuesta, para no delatar si la hay.
/// </summary>
public class ObtenerRecordatorioPropuestoConversacionQueryHandler(
    IComunicacionesQueryContext comunicacionesContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerRecordatorioPropuestoConversacionQuery, RecordatorioPropuestoDto?>
{
    public async Task<RecordatorioPropuestoDto?> Handle(
        ObtenerRecordatorioPropuestoConversacionQuery request, CancellationToken cancellationToken)
    {
        var conversacion = await comunicacionesContext.Conversaciones
            .Where(c => c.Id == request.ConversacionId)
            .Select(c => new { c.ClienteId, c.EmpresaId, c.ConexionIntegracionId, c.Estado, c.EstadoDesdeUtc })
            .FirstOrDefaultAsync(cancellationToken);

        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(
                conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return null;

        if (conversacion.Estado != EstadoConversacion.EsperandoRespuestaExterna)
            return null;

        var estadoDesdeUtc = conversacion.EstadoDesdeUtc;
        var propuesta = await comunicacionesContext.PropuestasRecordatorioConversacion
            .Where(p => p.ConversacionId == request.ConversacionId
                        && p.Estado == EstadoPropuestaRecordatorio.Pendiente
                        && p.EsperaDesdeUtc == estadoDesdeUtc)
            .Select(p => new RecordatorioPropuestoDto(p.Id, p.EsperaDesdeUtc, p.PropuestaEnUtc, null))
            .FirstOrDefaultAsync(cancellationToken);
        if (propuesta is null)
            return null;

        // Mismo filtro que el envío (ResponderConversacionCommandHandler.EnviarPorGraphAsync): responde al último entrante
        // con id externo, así que el «Para» que ve el Gestor CAE es el de verdad.
        var destinatario = await comunicacionesContext.Mensajes
            .Where(m => m.ConversacionId == request.ConversacionId
                        && m.Direccion == DireccionMensaje.Entrante
                        && m.MensajeExternoId != null)
            .OrderByDescending(m => m.FechaUtc)
            .Select(m => m.Remitente)
            .FirstOrDefaultAsync(cancellationToken);

        return propuesta with { Destinatario = destinatario };
    }
}
