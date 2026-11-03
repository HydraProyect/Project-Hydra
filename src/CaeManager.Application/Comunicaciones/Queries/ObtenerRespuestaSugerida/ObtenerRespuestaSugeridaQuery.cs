using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerRespuestaSugerida;

public record ObtenerRespuestaSugeridaQuery(Guid ConversacionId) : IRequest<RespuestaSugeridaDto?>;

public record RespuestaSugeridaDto(
    Guid Id, Guid ConversacionId, Guid MensajeOrigenId, OrigenRespuestaSugerida Origen, Guid? MacroId, string Texto,
    EstadoRespuestaSugerida Estado, Guid GeneradaPorUsuarioId, DateTime GeneradaEnUtc);

/// <summary>
/// Sugerencia activa de una conversación, o null. Consulta aparte del detalle, como las notas internas: es un borrador del
/// equipo y su audiencia es la de <see cref="IPoliticaNotaInterna"/>, más estrecha que la del hilo. Se deniega con null,
/// igual que un hilo sin sugerencia: la respuesta no delata si la hay.
///
/// <para>
/// Soporte TALVEG en una Sesión Privilegiada no la recibe, aunque esa política le deje leer la nota interna: es contenido de
/// un borrador dirigido a un tercero y <c>cae_app_soporte</c> no tiene concesión sobre la tabla. Se decide aquí, sin tocarla:
/// mejor un null que un 500 por un 42501.
/// </para>
/// </summary>
public class ObtenerRespuestaSugeridaQueryHandler(
    IComunicacionesQueryContext comunicaciones,
    IAlcanceDatosService alcanceDatos,
    IPoliticaNotaInterna politicaNotaInterna,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual)
    : IRequestHandler<ObtenerRespuestaSugeridaQuery, RespuestaSugeridaDto?>
{
    public async Task<RespuestaSugeridaDto?> Handle(ObtenerRespuestaSugeridaQuery request, CancellationToken cancellationToken)
    {
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null)
            return null;

        if (!await politicaNotaInterna.PuedeLeerAsync(cancellationToken))
            return null;

        var conversacion = await comunicaciones.Conversaciones.FirstOrDefaultAsync(c => c.Id == request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion, cancellationToken))
            return null;

        return await comunicaciones.RespuestasSugeridas
            .Where(r => r.ConversacionId == request.ConversacionId &&
                        (r.Estado == EstadoRespuestaSugerida.Propuesta || r.Estado == EstadoRespuestaSugerida.SinRevisar))
            .Select(r => new RespuestaSugeridaDto(
                r.Id, r.ConversacionId, r.MensajeOrigenId, r.Origen, r.MacroId, r.Texto, r.Estado, r.GeneradaPorUsuarioId, r.GeneradaEnUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
