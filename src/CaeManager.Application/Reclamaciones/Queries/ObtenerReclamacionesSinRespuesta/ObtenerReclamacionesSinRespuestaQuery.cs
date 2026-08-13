using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesSinRespuesta;

/// <summary>
/// Reclamaciones que salieron por una conversación y siguen sin contestar
/// pasados <paramref name="DiasSinRespuesta"/> días.
///
/// "Sin respuesta" es un estado DERIVADO, no persistido (docs/COMUNICACIONES.md
/// § 16.4): la conversación no tiene ningún mensaje entrante posterior al envío.
/// No se añade columna de estado ni se marca nada — si el cliente contesta, la
/// fila deja de aparecer sola.
///
/// Solo entran las reclamaciones con conversación: las enviadas sin buzón
/// conectado salieron por <c>IEmailService</c> y no hay forma de saber si
/// alguien respondió, así que afirmar "sin respuesta" sobre ellas sería mentir.
///
/// Una por Cliente, la más reciente: reclamar tres veces al mismo cliente es un
/// solo problema pendiente, no tres.
///
/// <paramref name="DiasSinRespuesta"/> llega por parámetro con un valor por
/// defecto en vez de por configuración: hoy solo hay un llamador y el umbral no
/// se ha discutido con nadie. Cuando alguien quiera moverlo por tenant, este es
/// el punto donde entra.
/// </summary>
public record ObtenerReclamacionesSinRespuestaQuery(int DiasSinRespuesta = 7)
    : IRequest<IReadOnlyList<ReclamacionSinRespuestaDto>>;

public record ReclamacionSinRespuestaDto(
    Guid ReclamacionId,
    Guid ClienteId,
    string RazonSocialCliente,
    Guid ConversacionId,
    DateTime FechaEnvioUtc,
    int DiasTranscurridos,
    int TotalDocumentos);

public class ObtenerReclamacionesSinRespuestaQueryHandler(
    IReclamacionesQueryContext reclamacionesContext,
    IComunicacionesQueryContext comunicacionesContext,
    IClientesQueryContext clientesContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerReclamacionesSinRespuestaQuery, IReadOnlyList<ReclamacionSinRespuestaDto>>
{
    public async Task<IReadOnlyList<ReclamacionSinRespuestaDto>> Handle(
        ObtenerReclamacionesSinRespuestaQuery request, CancellationToken cancellationToken)
    {
        var ahora = DateTime.UtcNow;
        var limite = ahora.AddDays(-request.DiasSinRespuesta);
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);

        var candidatas = await (
            from reclamacion in reclamacionesContext.ReclamacionesDocumentales
            where reclamacion.ConversacionId != null && reclamacion.FechaEnvioUtc <= limite
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(reclamacion.ClienteId)
            // Ningún entrante posterior al envío en ese hilo: la definición
            // completa de "esperando cliente" para esta reclamación.
            where !comunicacionesContext.Mensajes.Any(m =>
                m.ConversacionId == reclamacion.ConversacionId!.Value &&
                m.Direccion == DireccionMensaje.Entrante &&
                m.FechaUtc > reclamacion.FechaEnvioUtc)
            join cliente in clientesContext.Clientes on reclamacion.ClienteId equals cliente.Id
            select new
            {
                ReclamacionId = reclamacion.Id,
                reclamacion.ClienteId,
                cliente.RazonSocial,
                ConversacionId = reclamacion.ConversacionId!.Value,
                reclamacion.FechaEnvioUtc
            })
            .ToListAsync(cancellationToken);

        if (candidatas.Count == 0) return [];

        var ultimaPorCliente = candidatas
            .GroupBy(c => c.ClienteId)
            .Select(g => g.OrderByDescending(c => c.FechaEnvioUtc).First())
            .ToList();

        var reclamacionIds = ultimaPorCliente.Select(r => r.ReclamacionId).ToList();
        var totalesDocumentos = await reclamacionesContext.ReclamacionesDocumentalesDocumento
            .Where(d => reclamacionIds.Contains(d.ReclamacionDocumentalId))
            .GroupBy(d => d.ReclamacionDocumentalId)
            .Select(g => new { ReclamacionId = g.Key, Total = g.Count() })
            .ToDictionaryAsync(x => x.ReclamacionId, x => x.Total, cancellationToken);

        return ultimaPorCliente
            .Select(r => new ReclamacionSinRespuestaDto(
                r.ReclamacionId, r.ClienteId, r.RazonSocial, r.ConversacionId, r.FechaEnvioUtc,
                (int)(ahora - r.FechaEnvioUtc).TotalDays,
                totalesDocumentos.GetValueOrDefault(r.ReclamacionId)))
            // Lo que más lleva esperando primero: es el orden en que hay que atenderlo.
            .OrderByDescending(r => r.DiasTranscurridos)
            .ThenBy(r => r.RazonSocialCliente)
            .ToList();
    }
}
