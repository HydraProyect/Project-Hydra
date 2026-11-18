using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones;

/// <summary>
/// Las conversaciones que un listado puede mostrar, con la misma regla que la acción que abre cada una
/// (<see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/>, y con ella
/// <see cref="AlcanceDeMensajesDeConversacion.MensajeVisibleAsync"/>), traducida a SQL (D-6 b, R-3a). La usa la bandeja de
/// Comunicaciones (<c>ObtenerConversacionesQuery</c>), para que el listado no muestre lo que la acción rechaza ni al revés.
/// Las sugerencias de visita de Mi trabajo y de la Bandeja del Gestor CAE todavía no la usan: listan por Centro, y su
/// alineación con la acción va en un incremento propio, con su presupuesto de SQL. Son las dos mitades de
/// <c>ConversacionVisibleAsync</c>:
/// <list type="number">
/// <item>La regla por anclas de <see cref="VisibilidadConversacion"/> (<see cref="VisibilidadConversacion.Predicado{T}"/>),
/// la misma expresión que el detalle evalúa en memoria: cartera del Cliente empresarial, alcance de gestión de la Empresa
/// contraparte y, sin ancla, triaje o buzón personal propio.</item>
/// <item>La regla del buzón de <see cref="IAlcanceDatosService.ConexionIntegracionVisibleAsync"/>: fuera los hilos de un
/// buzón personal de otro Gestor CAE y los de una conexión eliminada, con la que el detalle falla cerrado. Las conexiones
/// se leen en una sentencia aparte con <c>IgnoreQueryFilters</c>, para ver también las eliminadas, y el Tenant se filtra a
/// mano (el filtro global combina Tenant y borrado lógico, e ignorarlo quita los dos); RLS sigue aplicando. No va dentro
/// de la consulta compuesta, porque ahí <c>IgnoreQueryFilters</c> quitaría también el filtro de Tenant de las
/// conversaciones. La lista de excluidas solo puede estrechar.</item>
/// </list>
/// Queda una diferencia, anterior a R-3a: una conversación cuya <c>ConexionIntegracionId</c> no existe (la columna no
/// tiene clave foránea) sigue en la lista, y el detalle la cierra.
/// </summary>
public static class ConversacionesVisibles
{
    public static async Task<IQueryable<Conversacion>> FiltrarAsync(
        IQueryable<Conversacion> conversaciones,
        IAlcanceDatosService alcanceDatos,
        ICurrentUserService currentUserService,
        IIntegracionesQueryContext integracionesContext,
        ITenantActual tenantActual,
        CancellationToken cancellationToken)
    {
        var visibilidad = await VisibilidadConversacion.ResolverAsync(alcanceDatos, cancellationToken);
        var visibles = conversaciones.Where(visibilidad.Predicado<Conversacion>(
            c => c.ClienteId, c => c.EmpresaId, c => c.ConexionIntegracionId));

        var excluidas = await ConexionesExcluidasAsync(currentUserService, integracionesContext, tenantActual, cancellationToken);
        return excluidas.Count == 0
            ? visibles
            : visibles.Where(c => c.ConexionIntegracionId == null || !excluidas.Contains(c.ConexionIntegracionId!.Value));
    }

    /// <summary>
    /// Las conexiones del Tenant actual cuyos hilos no se muestran: las eliminadas y los buzones personales de otro Gestor
    /// CAE (<c>GestorPropietarioId</c> distinto del usuario de la sesión; sin usuario, todos los personales). Sin Tenant
    /// actual no hay filas de Tenant que leer, y se usa la consulta con filtros, como antes.
    /// </summary>
    private static async Task<List<Guid>> ConexionesExcluidasAsync(
        ICurrentUserService currentUserService, IIntegracionesQueryContext integracionesContext, ITenantActual tenantActual,
        CancellationToken cancellationToken)
    {
        var usuarioActualId = await currentUserService.ObtenerUsuarioActualIdAsync();

        if (tenantActual.TenantId is not { } tenant)
            return await integracionesContext.ConexionesIntegracion
                .Where(c => c.GestorPropietarioId != null && c.GestorPropietarioId != usuarioActualId)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

        return await integracionesContext.ConexionesIntegracion
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenant
                        && (c.EstaEliminado || (c.GestorPropietarioId != null && c.GestorPropietarioId != usuarioActualId)))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
    }
}
