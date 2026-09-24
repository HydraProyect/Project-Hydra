using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using Microsoft.AspNetCore.Diagnostics;
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
/// <c>CurrentUserService.ObtenerRolEfectivoAsync</c>) — lo mismo vale para el
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
            bool sigueAutorizado;
            try
            {
                sigueAutorizado = await SigueAutorizadoAsync(
                    clienteActivoSeleccionado, currentUserService, dbContext, operacionesContext, sesionPrivilegiadaActual,
                    tenantSeleccionado, contexto.RequestAborted);
            }
            catch (OperationCanceledException) when (contexto.RequestAborted.IsCancellationRequested)
            {
                // El cliente abandonó la petición (navegación, cierre de pestaña,
                // circuito de Blazor que se desconecta) mientras esta consulta estaba
                // en vuelo. No es un 500: es exactamente el mismo caso que
                // Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware ya trata
                // sin ruido —499, log a Debug, sin invocar el manejador de errores—
                // pero solo cuando UseExceptionHandler está registrado, es decir, fuera
                // de Development (ver Program.cs). En Development no hay ese middleware
                // y la excepción subía cruda hasta convertirse en un "responded 500"
                // [ERR] con el que Serilog (y, en producción antes de esta guarda,
                // Sentry) no podían distinguir un cliente que se fue de un fallo real
                // del servidor (medido en el E2E local, log de WebAppFixture,
                // 2026-09-22: dos líneas así, ambas con esta excepción lanzada desde
                // SigueAutorizadoAsync/SigueAutorizadoPorAsignacionAsync).
                //
                // El "when" exige que sea *este* RequestAborted el que se disparó, no
                // cualquier OperationCanceledException — un timeout de comando u otra
                // cancelación ajena al cliente debe seguir subiendo como el error que
                // es. No hay más CancellationToken que este en las dos consultas de
                // abajo, así que hoy la condición siempre es cierta si el tipo de
                // excepción coincide; se deja explícita para no tener que revisarla el
                // día que eso deje de ser verdad.
                //
                // A Information, no a Debug: el objetivo pedido era que esto quedara
                // REGISTRADO como abortada, no que desapareciera. Con
                // Serilog:MinimumLevel:Default=Information de appsettings.json y sin
                // override para este namespace, un LogDebug aquí no llega a ningún
                // sink en ningún entorno real de esta app —a diferencia del
                // ExceptionHandlerMiddleware de framework, que si loguea a Debug es
                // porque corre bajo su propia configuración, no la de Serilog de esta
                // app (hallazgo de la revisión puente, 2026-09-23).
                RegistrarPeticionAbortada(contexto, logger);

                // Ni cookie ni `siguiente`: no hay nadie al otro lado a quien
                // escribirle una respuesta, y la selección seguía siendo válida —
                // no es una revocación real, así que no se toca.
                return;
            }

            // Una sola conversión al tipo concreto para las tres ramas que retiran la selección.
            var seleccionConcreta = clienteActivoSeleccionado as ClienteActivoSeleccionado;

            if (!sigueAutorizado && !PuedePintarElAviso(contexto.Request))
            {
                // Petición que no pinta una página (módulo JS, imagen,
                // /_blazor/initializers...): la selección se retira en ella
                // igual —el acceso ya está decidido y no se debilita—, pero la
                // cookie se conserva para que la próxima página vuelva a
                // revalidar, la retire allí y cuente por qué. Borrarla aquí
                // gastaba el único aviso en una respuesta que nadie lee: tras
                // caducar una ventana de soporte, una petición de fondo de la
                // página anterior llegaba primero, y la recarga caía en «Acceso
                // denegado» sin explicación (CI de main 83005ee3, 2026-09-24,
                // log de WebAppFixtureVentanaSoporte: /_blazor/initializers
                // invalidó la selección 5 ms antes de que /clientes respondiera
                // 302 hacia /acceso-denegado, que ya llegó sin cookie).
                logger.LogWarning(
                    "Selección de Workspace operativo derivado invalidada en {Ruta}: la revalidación no la autorizó. "
                    + "Tenant seleccionado {TenantSeleccionado}. Petición sin página: la cookie se conserva hasta la "
                    + "próxima, que la retira y avisa.",
                    contexto.Request.Path,
                    tenantSeleccionado);

                seleccionConcreta?.Invalidar();
            }
            else if (!sigueAutorizado)
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

                // Antes de Invalidar(): después ya no queda de qué vía venía. Sin
                // esto el usuario volvía a su organización sin una palabra y
                // «Organización principal» parecía un error de la aplicación.
                //
                // Esta segunda consulta comparte el mismo RequestAborted: si el
                // cliente se va justo aquí, es el mismo caso de arriba y se trata
                // igual (abortada, no error) en vez de dejarla escapar sin cazar.
                bool esVentanaDeSoporte;
                try
                {
                    esVentanaDeSoporte = await EsVentanaDeSoporteAsync(
                        clienteActivoSeleccionado, currentUserService, dbContext, tenantSeleccionado, contexto.RequestAborted);
                }
                catch (OperationCanceledException) when (contexto.RequestAborted.IsCancellationRequested)
                {
                    RegistrarPeticionAbortada(contexto, logger);

                    // A diferencia del catch de arriba: aquí !sigueAutorizado ya está
                    // decidido —EsVentanaDeSoporteAsync solo elige el texto del aviso,
                    // nunca la autorización—, así que invalidar no debe esperar a la
                    // próxima petición solo porque esta se abortó a medio camino.
                    // Es seguro hacerlo aquí y no antes de la consulta (hallazgo de la
                    // revisión puente, 2026-09-23): EsVentanaDeSoporteAsync solo puede
                    // cancelarse a mitad de un await, y sus dos únicas lecturas de
                    // SesionPrivilegiadaIdSeleccionada/AsignacionOperacionIdSeleccionada son
                    // síncronas y ocurren antes de cualquier await —si llegó a cancelarse
                    // es porque ya pasó esas dos lecturas, así que Invalidar() no les quita
                    // nada que aún no se hubiera consultado.
                    seleccionConcreta?.Invalidar();

                    contexto.Response.Cookies.Delete(ClienteActivoSeleccionado.NombreCookie);

                    return;
                }

                contexto.Items[AvisoFinDeAcceso.ClaveItems] = esVentanaDeSoporte
                    ? MotivoFinDeAcceso.VentanaDeSoporte
                    : MotivoFinDeAcceso.AccesoNoVigente;

                seleccionConcreta?.Invalidar();

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
    /// Único punto de registro para las dos ramas de aborto de arriba —misma línea
    /// de log, mismo código de estado, misma guarda contra la reejecución— para que
    /// no puedan divergir.
    ///
    /// Deshabilita <see cref="IStatusCodePagesFeature"/> a propósito (hallazgo de
    /// Codex, 2026-09-23): <c>UseStatusCodePagesWithReExecute("/not-found", ...)</c>
    /// está registrado en <c>Program.cs</c> antes que este middleware, y trata
    /// cualquier respuesta sin cuerpo entre 400 y 599 —499 incluido, no hay excepción
    /// para él— como "gestionable", reejecutando el pipeline entero hacia
    /// <c>/not-found</c>. Esa reejecución vuelve a entrar aquí con el mismo
    /// <see cref="HttpContext.RequestAborted"/>, ya cancelado, así que sin esta guarda
    /// se repetiría la consulta cancelada y este mismo log una segunda vez por la
    /// misma petición abortada.
    /// </summary>
    private static void RegistrarPeticionAbortada(HttpContext contexto, ILogger logger)
    {
        logger.LogInformation(
            "Revalidación de Workspace operativo derivado abortada en {Ruta}: el cliente canceló la petición.",
            contexto.Request.Path);

        if (!contexto.Response.HasStarted)
        {
            contexto.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;

            var statusCodePagesFeature = contexto.Features.Get<IStatusCodePagesFeature>();
            if (statusCodePagesFeature is not null)
                statusCodePagesFeature.Enabled = false;
        }
    }

    /// <summary>
    /// Si la respuesta a esta petición es una página que el usuario va a ver y
    /// que puede pintar <c>AvisoFinDeAccesoEstatico</c>: una navegación del
    /// navegador o una navegación mejorada de Blazor. Solo decide dónde se
    /// cuenta el aviso y se borra la cookie; nunca si la selección sigue
    /// valiendo.
    ///
    /// <para>
    /// Por orden: la navegación mejorada se reconoce por su marca en
    /// <c>Accept</c> (es un <c>fetch</c>, así que su <c>Sec-Fetch-Dest</c> no
    /// dice <c>document</c>). Si el navegador envía <c>Sec-Fetch-Dest</c>, solo
    /// <c>document</c> es una página: un <c>fetch</c> de fondo que pida
    /// <c>text/html</c> no lo es (hallazgo de Codex en #876). Sin esa cabecera
    /// (contexto no seguro, cliente que no la envía) se recurre a que pida
    /// <c>text/html</c>, que los recursos y las llamadas de fondo de Blazor no
    /// piden.
    /// </para>
    /// </summary>
    public static bool PuedePintarElAviso(HttpRequest peticion)
    {
        if (peticion.Path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase))
            return false;

        var aceptados = peticion.Headers.Accept;
        if (aceptados.Any(valor => valor?.Contains("blazor-enhanced-nav", StringComparison.OrdinalIgnoreCase) == true))
            return true;

        var destino = peticion.Headers["Sec-Fetch-Dest"];
        if (destino.Count > 0)
            return string.Equals(destino.ToString(), "document", StringComparison.OrdinalIgnoreCase);

        return aceptados.Any(valor => valor?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
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
    /// <summary>
    /// Si la selección que se retira era una ventana de soporte: una sesión
    /// privilegiada, o —en la vía heredada— delegaciones del usuario hacia ese
    /// tenant que son todas de propósito Soporte.
    /// Solo elige el texto del aviso; no interviene en la autorización.
    /// </summary>
    private static async Task<bool> EsVentanaDeSoporteAsync(
        IClienteActivoSeleccionado clienteActivoSeleccionado,
        ICurrentUserService currentUserService,
        ITenantsQueryContext dbContext,
        Guid tenantSeleccionado,
        CancellationToken cancellationToken)
    {
        if (clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null) return true;
        if (clienteActivoSeleccionado.AsignacionOperacionIdSeleccionada is not null) return false;

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return false;

        // La selección heredada no recuerda qué delegación la abrió, así que solo
        // se afirma «ventana de soporte» cuando no cabe otra lectura: el usuario
        // tiene delegación de Soporte hacia ese tenant y ninguna de otro
        // propósito. Con ambas a la vez el texto general —que siempre es cierto—
        // evita atribuir a la caducidad de Soporte lo que pudo ser otra revocación.
        var propositos = await (
            from asignacion in dbContext.AsignacionesOperadorDelegado
            join delegacion in dbContext.DelegacionesTenant on asignacion.DelegacionTenantId equals delegacion.Id
            where asignacion.UsuarioId == usuarioId.Value
                  && delegacion.TenantClienteId == tenantSeleccionado
            select delegacion.Proposito)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Distinct: «exactamente un propósito y es Soporte».
        return propositos is [PropositoDelegacion.Soporte];
    }

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
    /// Debe ir después de <c>UseAuthentication</c> —sin usuario resuelto no se
    /// puede comprobar de quién es el token— y antes de los endpoints y de
    /// los componentes. <b>No es cierto que vaya antes de que nada resuelva
    /// el tenant</b>: <c>UseRolEfectivoDelWorkspace</c> ya lo resolvió antes,
    /// y entre los dos hay <b>cuatro capas que pueden cortar la petición sin
    /// pasar por aquí</b> — <c>UseCuentaAMedioActivarSinAcceso</c>,
    /// <c>UseRateLimiter</c>, <c>UseAuthorization</c> y
    /// <c>UseAntiforgery</c> (ver <c>Program.cs</c>). Una petición que corte
    /// ahí no revalida, no borra la cookie y no emite ninguno de los dos
    /// avisos de este middleware (REC-189).
    ///
    /// <para>
    /// Eso <b>no</b> es un agujero de autorización: el fallo cerrado real —
    /// retirar el rol cuando la delegación ya no vale— lo impone
    /// <c>RolEfectivoDelWorkspaceMiddleware</c> en cada petición,
    /// independientemente de si este middleware llega a correr (ver
    /// <c>CurrentUserService.ObtenerRolEfectivoAsync</c>, que resuelve por las
    /// mismas tres vías que <see cref="RevalidacionClienteActivoMiddleware.SigueAutorizadoAsync"/>
    /// contra la misma base viva, no contra el token). Las dos consultas NO
    /// son idénticas condición por condición —la vía de asignación de
    /// operación en <c>ObtenerRolEfectivoAsync</c> no comprueba
    /// <c>operacion.VigenciaDesde</c>, y esta sí (REC-189, hallazgo
    /// secundario sin corregir aquí: está en <c>CurrentUserService</c>, fuera
    /// del alcance de este cambio)—, pero ninguna discrepancia entre las dos
    /// hace que <b>saltarse este middleware conceda algo que
    /// <c>RolEfectivoDelWorkspaceMiddleware</c> ya no hubiera concedido antes</b>,
    /// que es lo único que importa para que las cuatro capas intermedias no
    /// sean un agujero de autorización. Lo que se pierde en el hueco es
    /// diagnóstico y limpieza de cookie, no la comprobación que decide el
    /// acceso.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseRevalidacionClienteActivo(this IApplicationBuilder app) =>
        app.UseMiddleware<RevalidacionClienteActivoMiddleware>();
}
