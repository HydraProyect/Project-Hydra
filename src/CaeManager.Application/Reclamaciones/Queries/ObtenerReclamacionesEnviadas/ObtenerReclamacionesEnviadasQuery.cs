using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesEnviadas;

/// <summary>
/// Historial completo de lotes de reclamación enviados (Documentos, Parte
/// XVI PROMPT 03, pestaña "Reclamaciones" — seguimiento, no la vista de
/// componer/enviar que ya cubre <c>ObtenerLoteReclamacionQuery</c>). A
/// diferencia de <c>ObtenerUltimaReclamacionClienteQuery</c> (solo la más
/// reciente de un Cliente) y <c>ObtenerReclamacionesSinRespuestaQuery</c>
/// (solo las que llevan esperando, una por titular), esta lista TODOS los
/// lotes, de cualquier titular, paginados.
///
/// Incluye las de titular Empresa (documentos de empresa, DEC-7) además de
/// las de titular Cliente — es el historial completo, y ocultar la mitad
/// haría creer que nunca se reclamó. Cada rama se acota por SU cartera:
/// Clientes por ObtenerClienteIdsVisiblesAsync, Empresas por
/// ObtenerEmpresaIdsVisiblesAsync. Las dos anclas repuntan contra Empresas,
/// así que la razón social sale del mismo join en los dos casos.
/// </summary>
public record ObtenerReclamacionesEnviadasQuery(int Pagina = 1, int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<ReclamacionEnviadaDto>>;

/// <param name="SinRespuesta">
/// Igual que en ObtenerReclamacionesSinRespuestaQuery: derivado, no
/// persistido — null cuando la reclamación no tiene conversación asociada
/// (salió sin buzón conectado, o es anterior al vínculo con Comunicaciones)
/// y por tanto no hay forma de saber si alguien contestó.
/// </param>
/// <param name="AmbitoTitular">
/// <c>Cliente</c> o <c>Empresa</c> — decide a qué comando corresponde
/// "reclamar de nuevo" (EnviarReclamacionCommand o
/// EnviarReclamacionEmpresaCommand). Va en el DTO justamente porque
/// <paramref name="TitularId"/> alimenta después una escritura: sin él, el
/// reenvío trataría un titular Empresa como si fuera un Cliente y no
/// encontraría ni un documento reclamable.
/// </param>
/// <param name="DocumentoIds">
/// Los Documentos del lote que «Reclamar de nuevo» pide como lo que vence (el envío revalida la ventana). Un Documento que sigue
/// «Sin confirmar» y sin fecha no va aquí sino en <paramref name="Pendientes"/>, que es por donde el envío lo acepta.
/// </param>
/// <param name="Pendientes">
/// Lo que el lote pidió sin vencimiento y hoy sigue pendiente: los documentos que faltaban (línea sin Documento) y los
/// Documentos que siguen «Sin confirmar» sin fecha. Se reenvían tal cual: el envío los revalida contra la base.
/// <paramref name="TotalDocumentos"/> cuenta todas las líneas del lote, estén donde estén.
/// </param>
public record ReclamacionEnviadaDto(
    Guid Id, Guid TitularId, string TitularRazonSocial, AmbitoAplicacion AmbitoTitular, string DestinatarioEmail,
    DateTime FechaEnvioUtc, int TotalDocumentos, Guid? ConversacionId, bool? SinRespuesta,
    IReadOnlyList<Guid> DocumentoIds, IReadOnlyList<PendienteSinFecha>? Pendientes = null);

public class ObtenerReclamacionesEnviadasQueryHandler(
    IReclamacionesQueryContext reclamacionesContext,
    IEmpresasQueryContext empresasContext,
    IComunicacionesQueryContext comunicacionesContext,
    IAlcanceDatosService alcanceDatos,
    IPendientesDeReclamacionService pendientesDeReclamacion)
    : IRequestHandler<ObtenerReclamacionesEnviadasQuery, ResultadoPaginado<ReclamacionEnviadaDto>>
{
    public async Task<ResultadoPaginado<ReclamacionEnviadaDto>> Handle(
        ObtenerReclamacionesEnviadasQuery request, CancellationToken cancellationToken)
    {
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        // De gestión, no de lectura: el historial de lo que se le ha reclamado
        // a una contratista no es contenido de portal, y la cartera de
        // Empresas del rol Cliente sale de su propio Cliente.
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);

        var consulta =
            from reclamacion in reclamacionesContext.ReclamacionesDocumentales
            where (reclamacion.ClienteId != null &&
                   (clienteIdsVisibles == null || clienteIdsVisibles.Contains(reclamacion.ClienteId!.Value)))
               || (reclamacion.EmpresaId != null &&
                   (empresaIdsVisibles == null || empresaIdsVisibles.Contains(reclamacion.EmpresaId!.Value)))
            // Las dos anclas repuntan contra Empresas (ADR-011), así que el
            // titular se une por la que esté informada: el modelo garantiza
            // que es exactamente una (CK_ReclamacionesDocumentales_TitularUnico).
            join titular in empresasContext.Empresas
                on (reclamacion.ClienteId ?? reclamacion.EmpresaId) equals titular.Id
            select new { reclamacion, titular.RazonSocial };

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
                x.reclamacion.EmpresaId,
                RazonSocial = x.RazonSocial,
                x.reclamacion.DestinatarioEmail,
                x.reclamacion.FechaEnvioUtc,
                x.reclamacion.ConversacionId
            })
            .ToListAsync(cancellationToken);

        if (pagina.Count == 0)
            return new ResultadoPaginado<ReclamacionEnviadaDto>([], total, request.Pagina, request.TamanoPagina);

        var reclamacionIds = pagina.Select(r => r.Id).ToList();

        var lineas = await reclamacionesContext.ReclamacionesDocumentalesDocumento
            .Where(d => reclamacionIds.Contains(d.ReclamacionDocumentalId))
            .Select(d => new { d.ReclamacionDocumentalId, d.DocumentoId, d.TipoDocumentoId, d.TrabajadorId })
            .ToListAsync(cancellationToken);

        // De los Documentos de las líneas, cuáles siguen «Sin confirmar» sin fecha: esos se reenvían como pendientes (no entran
        // en la ventana por vencimiento). Se mira el estado de hoy, no el del día del envío: «Reclamar de nuevo» pide lo que
        // sigue pendiente ahora.
        var idsDeDocumentos = lineas.Where(l => l.DocumentoId is not null).Select(l => l.DocumentoId!.Value).Distinct().ToList();
        var sinConfirmarHoy = await pendientesDeReclamacion.DocumentoIdsAunSinConfirmarSinFechaAsync(idsDeDocumentos, cancellationToken);

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
                var lineasDe = lineas.Where(l => l.ReclamacionDocumentalId == r.Id).ToList();
                var documentoIds = lineasDe
                    .Where(l => l.DocumentoId is { } id && !sinConfirmarHoy.Contains(id))
                    .Select(l => l.DocumentoId!.Value)
                    .ToList();
                IReadOnlyList<PendienteSinFecha> pendientes =
                [
                    .. lineasDe.Where(l => l.DocumentoId is null).Select(l => PendienteSinFecha.Ausente(l.TrabajadorId, l.TipoDocumentoId!.Value)),
                    .. lineasDe.Where(l => l.DocumentoId is { } id && sinConfirmarHoy.Contains(id)).Select(l => PendienteSinFecha.SinConfirmar(l.DocumentoId!.Value))
                ];
                var sinRespuesta = RespuestaDeReclamacion.SinRespuesta(
                    r.ConversacionId,
                    r.FechaEnvioUtc,
                    r.ConversacionId is { } conversacionId ? entrantesPorConversacion.GetValueOrDefault(conversacionId, []) : []);

                return new ReclamacionEnviadaDto(
                    r.Id, r.ClienteId ?? r.EmpresaId!.Value, r.RazonSocial,
                    r.ClienteId is not null ? AmbitoAplicacion.Cliente : AmbitoAplicacion.Empresa,
                    r.DestinatarioEmail, r.FechaEnvioUtc,
                    lineasDe.Count, r.ConversacionId, sinRespuesta, documentoIds, pendientes);
            })
            .ToList();

        return new ResultadoPaginado<ReclamacionEnviadaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }
}
