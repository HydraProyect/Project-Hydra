using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesSinRespuesta;

/// <summary>
/// Reclamaciones que salieron por una conversación y siguen sin contestar
/// pasados <paramref name="DiasSinRespuesta"/> días.
///
/// "Sin respuesta" es un estado DERIVADO, no persistido (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md
/// § 16.4): la conversación no tiene ningún mensaje entrante posterior al envío.
/// No se añade columna de estado ni se marca nada — si el cliente contesta, la
/// fila deja de aparecer sola.
///
/// Solo entran las reclamaciones con conversación: las enviadas sin buzón
/// conectado salieron por <c>IEmailService</c> y no hay forma de saber si
/// alguien respondió, así que afirmar "sin respuesta" sobre ellas sería mentir.
///
/// Una por titular, la más reciente: reclamar tres veces al mismo titular es un
/// solo problema pendiente, no tres. Titular es (ancla, Id), no solo el Id:
/// una reclamación de documentos de Trabajador a una Empresa contraparte en
/// posición de cliente y una de documentos de empresa a esa MISMA Empresa son
/// dos pendientes distintos —otros documentos, otros destinatarios— y
/// colapsarlos por Id escondería uno de los dos. Con titular Empresa, además,
/// lo pedido de sus Trabajadores («Gestionar») se separa de lo pedido de la
/// propia Empresa, y por Delegación (<see cref="DestinoDeReclamacionesEnviadasService"/>).
///
/// <paramref name="DiasSinRespuesta"/> llega por parámetro con un valor por
/// defecto en vez de por configuración: hoy solo hay un llamador y el umbral no
/// se ha discutido con nadie. Cuando alguien quiera moverlo por tenant, este es
/// el punto donde entra.
/// </summary>
public record ObtenerReclamacionesSinRespuestaQuery(int DiasSinRespuesta = 7)
    : IRequest<IReadOnlyList<ReclamacionSinRespuestaDto>>;

/// <param name="AmbitoTitular"><c>Cliente</c> o <c>Empresa</c> — ver ObtenerReclamacionesEnviadasQuery, mismo criterio.</param>
/// <param name="EmpresaEmpleadora">
/// Informado cuando la reclamación pidió documentos de Trabajadores a su Empresa empleadora («Gestionar»), con la
/// Delegación a la que iría hoy; <c>null</c> en las de titular Cliente y en las de documentos de la propia Empresa.
/// </param>
public record ReclamacionSinRespuestaDto(
    Guid ReclamacionId,
    Guid TitularId,
    AmbitoAplicacion AmbitoTitular,
    string RazonSocialTitular,
    Guid ConversacionId,
    DateTime FechaEnvioUtc,
    int DiasTranscurridos,
    int TotalDocumentos,
    DestinoDeReclamacionEnviadaDto? EmpresaEmpleadora = null);

public class ObtenerReclamacionesSinRespuestaQueryHandler(
    IReclamacionesQueryContext reclamacionesContext,
    IComunicacionesQueryContext comunicacionesContext,
    IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos,
    DestinoDeReclamacionesEnviadasService destinoDeReclamaciones)
    : IRequestHandler<ObtenerReclamacionesSinRespuestaQuery, IReadOnlyList<ReclamacionSinRespuestaDto>>
{
    public async Task<IReadOnlyList<ReclamacionSinRespuestaDto>> Handle(
        ObtenerReclamacionesSinRespuestaQuery request, CancellationToken cancellationToken)
    {
        var ahora = DateTime.UtcNow;
        var limite = ahora.AddDays(-request.DiasSinRespuesta);
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        // De gestión, no de lectura: el historial de lo que se le ha reclamado
        // a una contratista no es contenido de portal, y la cartera de
        // Empresas del rol Cliente sale de su propio Cliente.
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);

        var candidatas = await (
            from reclamacion in reclamacionesContext.ReclamacionesDocumentales
            where reclamacion.ConversacionId != null && reclamacion.FechaEnvioUtc <= limite
            // Cada ancla contra SU cartera: una reclamación a una Empresa no la
            // ve quien no tiene esa Empresa asignada, aunque sí tenga al
            // Cliente del que cuelga en otro plano.
            where (reclamacion.ClienteId != null &&
                   (clienteIdsVisibles == null || clienteIdsVisibles.Contains(reclamacion.ClienteId!.Value)))
               || (reclamacion.EmpresaId != null &&
                   (empresaIdsVisibles == null || empresaIdsVisibles.Contains(reclamacion.EmpresaId!.Value)))
            // Ningún entrante posterior al envío en ese hilo: la definición
            // completa de "esperando cliente" para esta reclamación.
            where !comunicacionesContext.Mensajes.Any(m =>
                m.ConversacionId == reclamacion.ConversacionId!.Value &&
                m.Direccion == DireccionMensaje.Entrante &&
                m.FechaUtc > reclamacion.FechaEnvioUtc)
            // F3b — ClienteId repunta contra Empresas, y EmpresaId también:
            // el titular se une por el ancla informada, que es exactamente una.
            join titular in empresasContext.Empresas
                on (reclamacion.ClienteId ?? reclamacion.EmpresaId) equals titular.Id
            select new
            {
                ReclamacionId = reclamacion.Id,
                reclamacion.ClienteId,
                reclamacion.EmpresaId,
                titular.RazonSocial,
                ConversacionId = reclamacion.ConversacionId!.Value,
                reclamacion.FechaEnvioUtc
            })
            .ToListAsync(cancellationToken);

        if (candidatas.Count == 0) return [];

        // Con titular Empresa conviven dos pendientes distintos: lo pedido de la
        // propia Empresa (DEC-7) y lo pedido de sus Trabajadores («Gestionar»),
        // que además va a una Delegación u otra. Cada uno es su propio problema.
        var destinos = await destinoDeReclamaciones.DerivarAsync(
            [.. candidatas.Where(c => c.EmpresaId is not null).Select(c => c.ReclamacionId)], cancellationToken);

        var ultimaPorTitular = candidatas
            .GroupBy(c => new { c.ClienteId, c.EmpresaId, Destino = destinos.GetValueOrDefault(c.ReclamacionId) })
            .Select(g => g.OrderByDescending(c => c.FechaEnvioUtc).First())
            .ToList();

        var reclamacionIds = ultimaPorTitular.Select(r => r.ReclamacionId).ToList();
        var totalesDocumentos = await reclamacionesContext.ReclamacionesDocumentalesDocumento
            .Where(d => reclamacionIds.Contains(d.ReclamacionDocumentalId))
            .GroupBy(d => d.ReclamacionDocumentalId)
            .Select(g => new { ReclamacionId = g.Key, Total = g.Count() })
            .ToDictionaryAsync(x => x.ReclamacionId, x => x.Total, cancellationToken);

        return ultimaPorTitular
            .Select(r => new ReclamacionSinRespuestaDto(
                r.ReclamacionId, r.ClienteId ?? r.EmpresaId!.Value,
                r.ClienteId is not null ? AmbitoAplicacion.Cliente : AmbitoAplicacion.Empresa,
                r.RazonSocial, r.ConversacionId, r.FechaEnvioUtc,
                (int)(ahora - r.FechaEnvioUtc).TotalDays,
                totalesDocumentos.GetValueOrDefault(r.ReclamacionId),
                destinos.GetValueOrDefault(r.ReclamacionId)))
            // Lo que más lleva esperando primero: es el orden en que hay que atenderlo.
            .OrderByDescending(r => r.DiasTranscurridos)
            .ThenBy(r => r.RazonSocialTitular)
            .ToList();
    }
}
