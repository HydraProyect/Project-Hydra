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
/// crece con su número (dos agregaciones, sin consulta por fila).
///
/// <para>
/// <b>Visibilidad.</b> Los dos datos se calculan sobre las conversaciones que <see cref="ConversacionesVisibles.FiltrarAsync"/>
/// deja ver a quien pregunta (R-3a): la misma regla que la bandeja y que el detalle. Quien ve la bandeja entera ve aquí la
/// cifra entera; un Gestor CAE con una cartera acotada, solo la de sus hilos; y un buzón personal de otro Gestor CAE no sale en
/// la respuesta (la regla lo cierra también para el Administrador; la tabla enseña «—»). Esta petición no abre ningún hilo ni ningún dato que la
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

/// <param name="UltimoCorreoUtc">Instante del último correo recibido que quien pregunta puede ver; nulo si no hay ninguno.</param>
/// <param name="ConversacionesAbiertas">Conversaciones del buzón en estado «Abierta» que quien pregunta puede ver.</param>
public record ActividadBuzonDto(DateTime? UltimoCorreoUtc, int ConversacionesAbiertas);

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

        // El buzón personal de otro Gestor CAE no se mide: sus hilos no son visibles aquí, y un «0» o un «Sin correos aún»
        // afirmarían que está vacío. Queda fuera de la respuesta y la tabla enseña «—». Es la misma exclusión que hace
        // ConversacionesVisibles para los hilos, aplicada a la conexión.
        var usuarioActualId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var ajenos = await integracionesContext.ConexionesIntegracion
            .Where(c => ids.Contains(c.Id) && c.GestorPropietarioId != null && c.GestorPropietarioId != usuarioActualId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        ids = ids.Except(ajenos).ToList();
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
