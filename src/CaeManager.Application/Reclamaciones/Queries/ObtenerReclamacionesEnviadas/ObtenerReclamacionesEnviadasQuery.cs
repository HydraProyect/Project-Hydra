using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesEnviadas;

/// <summary>
/// Historial completo de lotes de reclamación enviados (Documentos, Parte
/// XVI PROMPT 03, pestaña "Reclamaciones" — seguimiento, no la vista de
/// componer/enviar que ya cubre <c>ObtenerLoteReclamacionQuery</c>). A
/// diferencia de <c>ObtenerUltimaReclamacionClienteQuery</c> (solo la más
/// reciente de un Cliente) y <c>ObtenerReclamacionesSinRespuestaQuery</c>
/// (solo las que llevan esperando, una por Cliente), esta lista TODOS los
/// lotes, de cualquier Cliente, paginados.
/// </summary>
public record ObtenerReclamacionesEnviadasQuery(int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<ReclamacionEnviadaDto>>;

/// <param name="SinRespuesta">
/// Igual que en ObtenerReclamacionesSinRespuestaQuery: derivado, no
/// persistido — null cuando la reclamación no tiene conversación asociada
/// (salió sin buzón conectado, o es anterior al vínculo con Comunicaciones)
/// y por tanto no hay forma de saber si alguien contestó.
/// </param>
public record ReclamacionEnviadaDto(
    Guid Id, Guid ClienteId, string ClienteRazonSocial, string DestinatarioEmail,
    DateTime FechaEnvioUtc, int TotalDocumentos, Guid? ConversacionId, bool? SinRespuesta,
    IReadOnlyList<Guid> DocumentoIds);

public class ObtenerReclamacionesEnviadasQueryHandler(
    IReclamacionesQueryContext reclamacionesContext,
    IClientesQueryContext clientesContext,
    IComunicacionesQueryContext comunicacionesContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerReclamacionesEnviadasQuery, ResultadoPaginado<ReclamacionEnviadaDto>>
{
    public async Task<ResultadoPaginado<ReclamacionEnviadaDto>> Handle(
        ObtenerReclamacionesEnviadasQuery request, CancellationToken cancellationToken)
    {
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);

        var consulta =
            from reclamacion in reclamacionesContext.ReclamacionesDocumentales
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(reclamacion.ClienteId)
            join cliente in clientesContext.Clientes on reclamacion.ClienteId equals cliente.Id
            select new { reclamacion, cliente.RazonSocial };

        var total = await consulta.CountAsync(cancellationToken);

        var pagina = await consulta
            .OrderByDescending(x => x.reclamacion.FechaEnvioUtc)
            .ThenBy(x => x.reclamacion.Id)
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new
            {
                x.reclamacion.Id,
                x.reclamacion.ClienteId,
                RazonSocial = x.RazonSocial,
                x.reclamacion.DestinatarioEmail,
                x.reclamacion.FechaEnvioUtc,
                x.reclamacion.ConversacionId
            })
            .ToListAsync(cancellationToken);

        if (pagina.Count == 0)
            return new ResultadoPaginado<ReclamacionEnviadaDto>([], total, request.Pagina, request.TamanoPagina);

        var reclamacionIds = pagina.Select(r => r.Id).ToList();

        var documentosPorReclamacion = await reclamacionesContext.ReclamacionesDocumentalesDocumento
            .Where(d => reclamacionIds.Contains(d.ReclamacionDocumentalId))
            .GroupBy(d => d.ReclamacionDocumentalId)
            .Select(g => new { ReclamacionId = g.Key, DocumentoIds = g.Select(d => d.DocumentoId).ToList() })
            .ToDictionaryAsync(g => g.ReclamacionId, g => g.DocumentoIds, cancellationToken);

        // Igual criterio que ObtenerReclamacionesSinRespuestaQuery, pero por
        // FILA (aquí una misma Conversación puede llevar más de un lote a
        // distintas fechas, así que el umbral "hay un entrante DESPUÉS del
        // envío" no puede resolverse con un simple Contains por conversación
        // — se trae la fecha de cada entrante y se compara en memoria).
        var conversacionIds = pagina.Where(r => r.ConversacionId is not null).Select(r => r.ConversacionId!.Value).ToList();
        var entrantesPorConversacion = conversacionIds.Count == 0
            ? new Dictionary<Guid, List<DateTime>>()
            : (await comunicacionesContext.Mensajes
                .Where(m => conversacionIds.Contains(m.ConversacionId) && m.Direccion == DireccionMensaje.Entrante)
                .Select(m => new { m.ConversacionId, m.FechaUtc })
                .ToListAsync(cancellationToken))
                .GroupBy(m => m.ConversacionId)
                .ToDictionary(g => g.Key, g => g.Select(m => m.FechaUtc).ToList());

        var elementos = pagina
            .Select(r =>
            {
                var documentoIds = documentosPorReclamacion.GetValueOrDefault(r.Id, []);
                bool? sinRespuesta = r.ConversacionId is null
                    ? null
                    : !entrantesPorConversacion.GetValueOrDefault(r.ConversacionId.Value, [])
                        .Any(fecha => fecha > r.FechaEnvioUtc);

                return new ReclamacionEnviadaDto(
                    r.Id, r.ClienteId, r.RazonSocial, r.DestinatarioEmail, r.FechaEnvioUtc,
                    documentoIds.Count, r.ConversacionId, sinRespuesta, documentoIds);
            })
            .ToList();

        return new ResultadoPaginado<ReclamacionEnviadaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
