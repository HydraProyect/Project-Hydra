using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciaVisitaCorreo;

/// <summary>Alimenta el prellenado de /visitas cuando el Gestor pulsa "Crear visita" desde una sugerencia en la Bandeja.</summary>
[AreaDelEncargo("Comunicaciones")]
public record ObtenerSugerenciaVisitaCorreoQuery(Guid Id) : IRequest<SugerenciaVisitaPrefillDto?>;

public record SugerenciaVisitaPrefillDto(Guid Id, Guid? CentroId, DateOnly? FechaInicio, DateOnly? FechaFin, string Resumen);

public class ObtenerSugerenciaVisitaCorreoQueryHandler(
    IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerSugerenciaVisitaCorreoQuery, SugerenciaVisitaPrefillDto?>
{
    public async Task<SugerenciaVisitaPrefillDto?> Handle(ObtenerSugerenciaVisitaCorreoQuery request, CancellationToken cancellationToken)
    {
        // Autorización sobre la conversación dueña del mensaje que originó la sugerencia, con la misma regla que el
        // detalle (ObtenerConversacionPorIdQuery, Issue #18) y que la acción a la que da paso, crear la Visita desde la
        // sugerencia (CrearVisitaCommand, con MensajeVisibleAsync): ConversacionVisibleAsync, que mira el buzón (un
        // buzón personal ajeno o una conexión eliminada cierran) y las anclas (D-6 b, R-3a). Antes solo miraba el Cliente
        // empresarial: abría el resumen de un hilo anclado de un buzón personal ajeno y cerraba el de un hilo de triaje
        // que la acción sí deja usar.
        var sugerencia = await (
            from s in comunicacionesContext.SugerenciasVisitaCorreo
            join m in comunicacionesContext.Mensajes on s.MensajeId equals m.Id
            join c in comunicacionesContext.Conversaciones on m.ConversacionId equals c.Id
            where s.Id == request.Id
            select new { s.Id, s.CentroId, s.FechaInicioSugerida, s.FechaFinSugerida, s.Resumen, c.ClienteId, c.EmpresaId, c.ConexionIntegracionId })
            .FirstOrDefaultAsync(cancellationToken);

        if (sugerencia is null) return null;

        if (!await alcanceDatos.ConversacionVisibleAsync(
                sugerencia.ClienteId, sugerencia.EmpresaId, sugerencia.ConexionIntegracionId, cancellationToken))
            return null;

        return new SugerenciaVisitaPrefillDto(sugerencia.Id, sugerencia.CentroId, sugerencia.FechaInicioSugerida, sugerencia.FechaFinSugerida, sugerencia.Resumen);
    }
}
