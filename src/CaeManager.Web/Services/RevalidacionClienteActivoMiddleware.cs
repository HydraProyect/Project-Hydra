using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Operaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Web.Services;

/// <summary>
/// Revalida el Delegated Workspace activo una vez por petición, fuera del
/// camino caliente del filtro global (hallazgo N-6 de INFORME-AUDITORIA-2.md).
///
/// El token de selección se comprueba <b>al emitirse</b> y su lectura no hace
/// I/O a propósito: <c>ITenantActual.TenantId</c> se evalúa dentro de
/// <c>HasQueryFilter</c> y consultar ahí sería una regresión de rendimiento
/// severa (ver <see cref="ClienteActivoSeleccionado"/>). La consecuencia era
/// que, revocada una delegación, un token vivo seguía dando acceso al
/// ex-cliente hasta que caducaba. Asimetría reveladora: el operador revocado
/// ya desaparecía del selector —<c>ObtenerClientesAutorizadosQuery</c> sí
/// revalida en cada render— mientras conservaba el acceso real.
///
/// Aquí es el sitio correcto: una sola consulta por petición HTTP, y solo
/// para quien trae cookie de selección, es decir solo para Operadores
/// Delegados. Para todos los demás el middleware no hace nada.
///
/// Limitación conocida: un circuito de Blazor Server ya establecido puede
/// seguir interactuando por SignalR sin generar peticiones HTTP nuevas, así
/// que la revocación se nota en la siguiente navegación, no necesariamente al
/// instante. Lo que sí es inmediato es la escritura: el rol efectivo pasa a
/// null en cuanto la delegación deja de estar activa y
/// <c>AutorizacionEscrituraBehavior</c> bloquea por lista blanca (ver
/// <c>CurrentUserService.ObtenerRolActualAsync</c>).
/// </summary>
public class RevalidacionClienteActivoMiddleware(RequestDelegate siguiente)
{
    public async Task InvokeAsync(
        HttpContext contexto,
        IClienteActivoSeleccionado clienteActivoSeleccionado,
        ICurrentUserService currentUserService,
        ITenantsQueryContext dbContext,
        IOperacionesQueryContext operacionesContext)
    {
        // Sin cookie no hay nada que revalidar: cero coste para el usuario
        // normal, que es la inmensa mayoría.
        if (string.IsNullOrEmpty(contexto.Request.Cookies[ClienteActivoSeleccionado.NombreCookie]))
        {
            await siguiente(contexto);
            return;
        }

        // Se lee de la propia abstracción, no de la cookie cruda: un token
        // manipulado, caducado o de otro usuario ya resuelve a null ahí.
        if (clienteActivoSeleccionado.TenantIdSeleccionado is { } tenantSeleccionado)
        {
            var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();

            // Dos caminos porque conviven dos mecanismos: la vía nueva (una
            // AsignacionOperacion en el token) y la heredada, que es la del
            // acceso de soporte hasta su propia fase. Conmutar el middleware
            // entero a la vía nueva habría dejado al soporte sin acceso durante
            // toda la transición.
            var sigueAutorizado = usuarioId is not null && (
                clienteActivoSeleccionado.AsignacionOperacionIdSeleccionada is { } asignacionOperacionId
                    ? await SigueAutorizadoPorAsignacionAsync(
                        operacionesContext, usuarioId.Value, tenantSeleccionado, asignacionOperacionId, contexto.RequestAborted)
                    : await SigueAutorizadoPorDelegacionAsync(
                        dbContext, usuarioId.Value, tenantSeleccionado, contexto.RequestAborted));

            if (!sigueAutorizado)
            {
                if (clienteActivoSeleccionado is ClienteActivoSeleccionado seleccion)
                    seleccion.Invalidar();

                contexto.Response.Cookies.Delete(ClienteActivoSeleccionado.NombreCookie);
            }
        }
        else
        {
            // El token ya no vale por sí solo (caducó, se manipuló, es de
            // otro usuario): la cookie sobra y arrastrarla solo genera este
            // trabajo en cada petición.
            contexto.Response.Cookies.Delete(ClienteActivoSeleccionado.NombreCookie);
        }

        await siguiente(contexto);
    }

    /// <summary>
    /// Vía nueva. Comprueba tres cosas, y las tres hacen falta:
    /// <list type="number">
    /// <item>la <b>coherencia</b> entre los dos campos del token — la operación
    /// referenciada tiene que pertenecer al tenant que el token dice, o un
    /// token con un tenant de aquí y una operación de allá abriría un contexto
    /// que nadie autorizó;</item>
    /// <item>que la <b>operación</b> siga vigente;</item>
    /// <item>que el <b>usuario</b> tenga cartera vigente bajo ella. Sin esto,
    /// retirar a un usuario de la cartera no le cortaría el acceso hasta que
    /// caducara su token — hasta 8 horas después. Es exactamente el agujero que
    /// esta revalidación cerró en su día, y comprobar solo la operación lo
    /// habría reabierto.</item>
    /// </list>
    /// </summary>
    private static async Task<bool> SigueAutorizadoPorAsignacionAsync(
        IOperacionesQueryContext operacionesContext,
        Guid usuarioId, Guid tenantSeleccionado, Guid asignacionOperacionId, CancellationToken cancellationToken)
    {
        var ahora = DateTime.UtcNow;

        return await (
            from cartera in operacionesContext.AsignacionesCartera
            join operacion in operacionesContext.AsignacionesOperacion
                on cartera.AsignacionOperacionId equals operacion.Id
            where cartera.AsignacionOperacionId == asignacionOperacionId
                  && cartera.UsuarioId == usuarioId
                  && cartera.Estado == EstadoAsignacion.Vigente
                  && cartera.VigenciaDesde <= ahora
                  && (cartera.VigenciaHasta == null || ahora < cartera.VigenciaHasta)
                  && operacion.PropietarioTenantId == tenantSeleccionado
                  && operacion.Estado == EstadoAsignacion.Vigente
                  && operacion.VigenciaDesde <= ahora
                  && (operacion.VigenciaHasta == null || ahora < operacion.VigenciaHasta)
            select cartera.Id)
            .AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Vía heredada, la del acceso de soporte. Se conserva intacta: su
    /// reclasificación al plano de privilegio de plataforma es una fase
    /// posterior, y tocarla aquí habría mezclado dos migraciones.
    /// </summary>
    private static Task<bool> SigueAutorizadoPorDelegacionAsync(
        ITenantsQueryContext dbContext, Guid usuarioId, Guid tenantSeleccionado, CancellationToken cancellationToken) =>
        (from asignacion in dbContext.AsignacionesOperadorDelegado
         join delegacion in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
         where asignacion.UsuarioId == usuarioId
               // Activa y no caducada: es lo que hace que una ventana
               // de soporte vencida corte el acceso en la siguiente
               // petición, sin que nadie tenga que revocarla a mano
               // (ver DelegacionTenant.EstaVigente).
               && delegacion.Activa
               && (delegacion.ExpiraEnUtc == null || delegacion.ExpiraEnUtc > DateTime.UtcNow)
               && delegacion.TenantClienteId == tenantSeleccionado
         select delegacion.Id)
        .AnyAsync(cancellationToken);
}

public static class RevalidacionClienteActivoMiddlewareExtensions
{
    /// <summary>
    /// Debe ir después de <c>UseAuthentication</c> — sin usuario resuelto no
    /// se puede comprobar de quién es el token — y antes de que nada resuelva
    /// el tenant, es decir antes de los endpoints y de los componentes.
    /// </summary>
    public static IApplicationBuilder UseRevalidacionClienteActivo(this IApplicationBuilder app) =>
        app.UseMiddleware<RevalidacionClienteActivoMiddleware>();
}
