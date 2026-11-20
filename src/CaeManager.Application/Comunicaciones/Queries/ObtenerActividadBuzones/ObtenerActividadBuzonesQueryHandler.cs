using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerActividadBuzones;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerActividadBuzones;

/// <summary>
/// Atiende <see cref="ObtenerActividadBuzonesQuery"/> (contrato de lectura del Núcleo, definido en Application.Integraciones)
/// sobre las conversaciones y mensajes de Comunicaciones, siempre dentro de lo que <see cref="ConversacionesVisibles"/> deja ver.
/// </summary>
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
