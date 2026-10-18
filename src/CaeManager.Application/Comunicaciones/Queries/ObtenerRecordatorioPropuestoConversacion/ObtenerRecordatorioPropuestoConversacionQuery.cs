using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerRecordatorioPropuestoConversacion;

public record ObtenerRecordatorioPropuestoConversacionQuery(Guid ConversacionId) : IRequest<RecordatorioPropuestoDto?>;

public record RecordatorioPropuestoDto(Guid PropuestaId, DateTime EsperaDesdeUtc, DateTime PropuestaEnUtc);

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
        return await comunicacionesContext.PropuestasRecordatorioConversacion
            .Where(p => p.ConversacionId == request.ConversacionId
                        && p.Estado == EstadoPropuestaRecordatorio.Pendiente
                        && p.EsperaDesdeUtc == estadoDesdeUtc)
            .Select(p => new RecordatorioPropuestoDto(p.Id, p.EsperaDesdeUtc, p.PropuestaEnUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
