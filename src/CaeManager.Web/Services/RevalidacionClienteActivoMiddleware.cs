using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
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
        ITenantsQueryContext dbContext)
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

            var sigueAutorizado = usuarioId is not null && await (
                from asignacion in dbContext.AsignacionesOperadorDelegado
                join delegacion in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
                where asignacion.UsuarioId == usuarioId.Value
                      // Activa y no caducada: es lo que hace que una ventana
                      // de soporte vencida corte el acceso en la siguiente
                      // petición, sin que nadie tenga que revocarla a mano
                      // (ver DelegacionTenant.EstaVigente).
                      && delegacion.Activa
                      && (delegacion.ExpiraEnUtc == null || delegacion.ExpiraEnUtc > DateTime.UtcNow)
                      && delegacion.TenantClienteId == tenantSeleccionado
                select delegacion.Id)
                .AnyAsync(contexto.RequestAborted);

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
