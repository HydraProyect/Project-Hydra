using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerActividadBuzones;

/// <summary>
/// Los dos datos de actividad que la tabla de Buzones enseña por buzón (columnas «Último correo» y «Abiertas», lienzo
/// «Comunicaciones multi-buzón», tablero de Buzones). Una sola petición para todos los buzones de la página: el coste no
/// crece con su número ni hay consulta por fila. Son cuatro sentencias fijas: los buzones medibles, las conexiones que
/// <see cref="ConversacionesVisibles.FiltrarAsync"/> excluye, las abiertas y los últimos correos, más las que el alcance
/// necesite para resolverse (memoizado en la petición); el techo medido en PostgreSQL lo fija
/// <c>ObtenerActividadBuzonesQueryBajoRuntimeTests</c>. La página la pide por el puerto del Núcleo
/// <see cref="IActividadBuzones"/> (<see cref="ActividadBuzonesService"/>), no directamente (ADR-013).
///
/// <para>
/// <b>Visibilidad.</b> Los dos datos se calculan sobre las conversaciones que <see cref="ConversacionesVisibles.FiltrarAsync"/>
/// deja ver a quien pregunta (R-3a): la misma regla que la bandeja y que el detalle. Quien ve la bandeja entera ve aquí la
/// cifra entera; un Gestor CAE con una cartera acotada, solo la de sus hilos; un Tenant solo Inbound, solo los hilos anclados a
/// una Empresa (la petición es de Comunicaciones y corre con el ámbito de D6=a). Solo vuelven claves para los buzones medibles
/// del Tenant actual (los compartidos y el personal propio): un buzón personal de otro Gestor CAE (la regla lo cierra también
/// para el Administrador), uno eliminado, uno de otro Tenant o un id que no existe faltan de la respuesta, y la tabla enseña
/// «—». Esta petición no abre ningún hilo ni ningún dato que la
/// bandeja no abra: solo agrega lo ya visible. No toca la autorización.
/// </para>
///
/// <para>
/// <b>«Abiertas»</b> son las conversaciones del buzón cuyo estado es <see cref="EstadoConversacion.Abierta"/>: el estado
/// «Abierta» que la bandeja enseña y filtra (<c>ObtenerConversacionesQuery.Estado</c>). No cuentan las que esperan respuesta
/// externa o a un tercero, ni las resueltas ni las cerradas. <b>«Último correo»</b> es la fecha del último mensaje
/// <see cref="DireccionMensaje.Entrante"/> de esas conversaciones visibles, por la conexión por la que entran
/// (<c>Conversacion.ConexionIntegracionId</c>); un acuse o una respuesta nuestra no son un correo recibido.
/// </para>
/// </summary>
[AreaDelEncargo("Comunicaciones")]
public record ObtenerActividadBuzonesQuery(IReadOnlyCollection<Guid> ConexionIds)
    : IRequest<IReadOnlyDictionary<Guid, ActividadBuzonDto>>;

/// <summary>
/// Implementación en Outbound del puerto <see cref="IActividadBuzones"/> del Núcleo: envía <see cref="ObtenerActividadBuzonesQuery"/>
/// por el mediador (ISender, el mismo que resuelve el contenedor para IMediator) para que la petición pase por la guarda y el ámbito de Outbound.
/// </summary>
public sealed class ActividadBuzonesService(ISender mediador) : IActividadBuzones
{
    public Task<IReadOnlyDictionary<Guid, ActividadBuzonDto>> ObtenerAsync(
        IReadOnlyCollection<Guid> conexionIds, CancellationToken cancellationToken = default) =>
        mediador.Send(new ObtenerActividadBuzonesQuery(conexionIds), cancellationToken);
}

public class ObtenerActividadBuzonesQueryHandler(
    IComunicacionesQueryContext comunicacionesContext, IIntegracionesQueryContext integracionesContext,
    IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService, ITenantActual tenantActual)
    : IRequestHandler<ObtenerActividadBuzonesQuery, IReadOnlyDictionary<Guid, ActividadBuzonDto>>
{
    public async Task<IReadOnlyDictionary<Guid, ActividadBuzonDto>> Handle(
        ObtenerActividadBuzonesQuery request, CancellationToken cancellationToken)
    {
        var ids = request.ConexionIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, ActividadBuzonDto>();

        // Solo se miden los buzones del Tenant actual que se pueden medir: los compartidos y el personal propio. La lectura va
        // con el filtro de Tenant y de borrado lógico (y RLS): un id de otro Tenant, uno que no existe o una conexión
        // eliminada no vuelven, y tampoco el buzón personal de otro Gestor CAE, cuyos hilos no son visibles aquí (un «0» o un
        // «Sin correos visibles» afirmarían que está vacío; es la misma exclusión que hace ConversacionesVisibles para los hilos,
        // aplicada a la conexión). Esos ids faltan de la respuesta y la tabla enseña «—».
        var usuarioActualId = await currentUserService.ObtenerUsuarioActualIdAsync();
        ids = await integracionesContext.ConexionesIntegracion
            .Where(c => ids.Contains(c.Id) && (c.GestorPropietarioId == null || c.GestorPropietarioId == usuarioActualId))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0) return new Dictionary<Guid, ActividadBuzonDto>();

        var visibles = await ConversacionesVisibles.FiltrarAsync(
            comunicacionesContext.Conversaciones, alcanceDatos, currentUserService, integracionesContext, tenantActual,
            cancellationToken);
        var deLosBuzones = visibles.Where(c => c.ConexionIntegracionId != null && ids.Contains(c.ConexionIntegracionId.Value));

        var abiertas = await deLosBuzones
            .Where(c => c.Estado == EstadoConversacion.Abierta)
            .GroupBy(c => c.ConexionIntegracionId!.Value)
            .Select(g => new { ConexionId = g.Key, Total = g.Count() })
            .ToListAsync(cancellationToken);

        var ultimos = await (
            from m in comunicacionesContext.Mensajes
            where m.Direccion == DireccionMensaje.Entrante
            join c in deLosBuzones on m.ConversacionId equals c.Id
            group m by c.ConexionIntegracionId!.Value into g
            select new { ConexionId = g.Key, Ultimo = g.Max(m => m.FechaUtc) })
            .ToListAsync(cancellationToken);

        var totales = abiertas.ToDictionary(a => a.ConexionId, a => a.Total);
        var fechas = ultimos.ToDictionary(u => u.ConexionId, u => u.Ultimo);
        return ids.ToDictionary(
            id => id,
            id => new ActividadBuzonDto(
                fechas.TryGetValue(id, out var ultimo) ? ultimo : null,
                totales.GetValueOrDefault(id)));
    }
}
