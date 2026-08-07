using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciaVisitaCorreo;

/// <summary>Alimenta el prellenado de /visitas cuando el Gestor pulsa "Crear visita" desde una sugerencia en la Bandeja.</summary>
public record ObtenerSugerenciaVisitaCorreoQuery(Guid Id) : IRequest<SugerenciaVisitaPrefillDto?>;

public record SugerenciaVisitaPrefillDto(Guid Id, Guid? CentroId, DateOnly? FechaInicio, DateOnly? FechaFin, string Resumen);

public class ObtenerSugerenciaVisitaCorreoQueryHandler(
    IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSugerenciaVisitaCorreoQuery, SugerenciaVisitaPrefillDto?>
{
    public async Task<SugerenciaVisitaPrefillDto?> Handle(ObtenerSugerenciaVisitaCorreoQuery request, CancellationToken cancellationToken)
    {
        // Autorización sobre el Cliente de la conversación dueña del mensaje
        // que originó la sugerencia — mismo criterio que ObtenerConversacionPorIdQuery (Issue #18).
        var sugerencia = await (
            from s in comunicacionesContext.SugerenciasVisitaCorreo
            join m in comunicacionesContext.Mensajes on s.MensajeId equals m.Id
            join c in comunicacionesContext.Conversaciones on m.ConversacionId equals c.Id
            where s.Id == request.Id
            select new { s.Id, s.CentroId, s.FechaInicioSugerida, s.FechaFinSugerida, s.Resumen, c.ClienteId })
            .FirstOrDefaultAsync(cancellationToken);

        if (sugerencia is null) return null;

        if (sugerencia.ClienteId is null || !await alcanceDatos.ClienteVisibleAsync(sugerencia.ClienteId.Value, cancellationToken))
            return null;

        return new SugerenciaVisitaPrefillDto(sugerencia.Id, sugerencia.CentroId, sugerencia.FechaInicioSugerida, sugerencia.FechaFinSugerida, sugerencia.Resumen);
    }
}
