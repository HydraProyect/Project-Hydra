using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
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
/// Limitación que tenía esta pieza en solitario: un circuito de Blazor
/// Server ya establecido puede seguir interactuando por SignalR sin generar
/// peticiones HTTP nuevas, así que la revocación no se notaba hasta la
/// siguiente navegación (hallazgo del Módulo 9, auditoría 2026-08-30). Lo
/// que ya era inmediato es la escritura: el rol efectivo pasa a null en
/// cuanto la delegación deja de estar activa y
/// <c>AutorizacionEscrituraBehavior</c> bloquea por lista blanca (ver
/// <c>CurrentUserService.ObtenerRolActualAsync</c>) — lo mismo vale para el
/// tercer camino, una sesión privilegiada cerrada o cuya concesión se
/// revocó, porque ese behavior la revalida contra la base en cada Command.
/// La ventana que quedaba abierta era solo de <b>lectura</b> dentro de un
/// circuito ya vivo, y la cierra <see cref="RevalidacionCircuitoActivoHandler"/>
/// repitiendo <see cref="SigueAutorizadoAsync"/> —la misma comprobación,
/// nunca una copia que pueda divergir— desde un temporizador de fondo del
/// propio circuito.
/// </summary>
public class RevalidacionClienteActivoMiddleware(RequestDelegate siguiente)
{
    public async Task InvokeAsync(
        HttpContext contexto,
        IClienteActivoSeleccionado clienteActivoSeleccionado,
        ICurrentUserService currentUserService,
        ITenantsQueryContext dbContext,
        IOperacionesQueryContext operacionesContext,
        ISesionPrivilegiadaActual sesionPrivilegiadaActual,
        ILogger<RevalidacionClienteActivoMiddleware> logger)
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
            var sigueAutorizado = await SigueAutorizadoAsync(
                clienteActivoSeleccionado, currentUserService, dbContext, operacionesContext, sesionPrivilegiadaActual,
                tenantSeleccionado, contexto.RequestAborted);

            if (!sigueAutorizado)
            {
                // Se registra porque hasta REC-110 esto ocurría sin dejar rastro
                // alguno: retirar el Workspace operativo derivado en mitad de una
                // sesión legítima es indistinguible, desde fuera, de un tenant
                // que de verdad no tiene datos — las dos cosas se ven como una
                // lista vacía. Diagnosticar el intermitente de
                // SeleccionSobreviveAlCircuitoTests obligó a deducir por el
                // tiempo de respuesta de la petición cuál de las dos había
                // pasado, porque el sistema no lo decía en ninguna parte. Aviso,
                // no error: invalidar es el comportamiento correcto cuando la
                // autorización ya no está viva; lo que faltaba era poder
                // distinguir ese caso del que no lo es.
                logger.LogWarning(
                    "Selección de Workspace operativo derivado invalidada en {Ruta}: la revalidación no la autorizó. "
                    + "Tenant seleccionado {TenantSeleccionado}, vía {Via}.",
                    contexto.Request.Path,
                    tenantSeleccionado,
                    clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null
                        ? "sesión privilegiada"
                        : clienteActivoSeleccionado.AsignacionOperacionIdSeleccionada is not null
                            ? "asignación de operación"
                            : "delegación heredada");

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
            //
            // REC-136: esta rama borraba la cookie sin dejar ninguna traza,
            // indistinguible desde fuera de la rama `!sigueAutorizado` de
            // arriba —que sí avisa— salvo por el mensaje. Se distingue de esa
            // otra en que aquí `TenantIdSeleccionado` ya resolvió a null
            // *antes* de intentar revalidar nada: el token no se descifró, no
            // tiene el formato esperado, o no está ligado a este usuario (ver
            // `ClienteActivoSeleccionado.LeerCargaUtil`), así que no hay
            // tenant seleccionado que citar en el aviso, a diferencia del de
            // `:83`. Nivel Warning, igual que el otro camino que borra la
            // misma cookie: los dos son el mismo evento observable desde
            // fuera (cookie presente, selección perdida) y deben poder
            // distinguirse en el mismo artefacto de CI.
            logger.LogWarning(
                "Selección de Workspace operativo derivado descartada en {Ruta}: la cookie está presente pero el "
                + "token no resolvió a ningún tenant (no se pudo descifrar, formato inesperado, o no ligado al "
                + "usuario actual).",
                contexto.Request.Path);

            contexto.Response.Cookies.Delete(ClienteActivoSeleccionado.NombreCookie);
        }

        await siguiente(contexto);
    }

    /// <summary>
    /// La comprobación completa, factorizada para que
    /// <see cref="RevalidacionCircuitoActivoHandler"/> pueda repetirla desde
    /// dentro del circuito sin duplicar la lógica de autorización — dos
    /// sitios que decidieran esto por separado son dos sitios que pueden
    /// dejar de coincidir.
    /// </summary>
    internal static async Task<bool> SigueAutorizadoAsync(
        IClienteActivoSeleccionado clienteActivoSeleccionado,
        ICurrentUserService currentUserService,
        ITenantsQueryContext dbContext,
        IOperacionesQueryContext operacionesContext,
        ISesionPrivilegiadaActual sesionPrivilegiadaActual,
        Guid tenantSeleccionado,
        CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();

        // Tres caminos, uno por vía de acceso, y excluyentes entre sí
        // (ADR-011 § 4bis.5 — las capacidades no se acumulan entre planos):
        // la sesión privilegiada de plataforma; la operación de plano 2; y
        // la heredada por delegación, que es la del acceso de soporte
        // actual hasta que se retire. Conmutar el middleware entero a una
        // sola habría dejado sin acceso a las otras durante la transición.
        //
        // El plano 3 se comprueba primero porque es el más restrictivo y el
        // que no debe caer nunca al camino de negocio: si el token nombra
        // una sesión y esa sesión no revalida, la respuesta es cortar, no
        // probar suerte con la delegación heredada del mismo usuario.
        return usuarioId is not null && (
            clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null
                // ObtenerAsync ya comprueba las cuatro condiciones: sesión
                // abierta y en ventana, ligada a este usuario, concesión
                // vigente, y tenant todavía en su alcance y coherente con el
                // que el token declara.
                ? await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null
                : clienteActivoSeleccionado.AsignacionOperacionIdSeleccionada is { } asignacionOperacionId
                    ? await SigueAutorizadoPorAsignacionAsync(
                        operacionesContext, usuarioId.Value, tenantSeleccionado, asignacionOperacionId, cancellationToken)
                    : await SigueAutorizadoPorDelegacionAsync(
                        dbContext, usuarioId.Value, tenantSeleccionado, cancellationToken));
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
