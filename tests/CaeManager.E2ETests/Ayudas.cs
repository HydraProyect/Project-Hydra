using System.Security.Cryptography;
using ClosedXML.Excel;
using Microsoft.Playwright;
using PdfSharp.Pdf;

namespace CaeManager.E2ETests;

/// <summary>
/// Datos y utilidades compartidas por los tests E2E — credenciales de los
/// usuarios sembrados por IdentitySeeder/DatosPruebaSeeder (ver esas clases
/// en CaeManager.Infrastructure.Identity / Persistence.Seed) y helpers de
/// Playwright para los patrones repetidos de login/drawer que ya se
/// verificaban a mano con verificar_roles.js.
/// </summary>
public static class Ayudas
{
    public const string EmailAdministrador = "admin@caemanager.local";
    public const string ContrasenaAdministrador = "CaeManager#2026";

    /// <summary>
    /// Misma clave que IdentitySeeder.ClaveTotpAdministradorInicial (ver esa
    /// clase en CaeManager.Infrastructure.Identity) — duplicada aquí en vez
    /// de referenciada porque este proyecto de test no referencia
    /// Infrastructure (mismo criterio que NombreClienteDelegadoDemo); si
    /// cambia allí, este test debe actualizarse también. El Administrador
    /// inicial nace con 2FA activo (P1-13 de docs/business/MATURITY_REVIEW.md),
    /// así que IniciarSesionAsync tiene que poder calcular el código TOTP.
    /// </summary>
    public const string ClaveTotpAdministrador = "JBSWY3DPEHPK3PXP";

    public const string ContrasenaUsuariosPrueba = "Prueba#2026";

    /// <summary>
    /// Nombre del tenant Cliente Delegante que DelegacionDemoSeeder siembra
    /// para el Administrador inicial (ver esa clase en
    /// CaeManager.Infrastructure.Persistence.Seed) — duplicado aquí en vez de
    /// referenciado porque este proyecto de test no referencia Infrastructure
    /// (mismo criterio que EmailAdministradorSegundoTenant); si cambia allí,
    /// este test debe actualizarse también.
    /// </summary>
    public const string NombreClienteDelegadoDemo = "Laboratorios Dexter S.L. (Cliente Delegante demo)";

    /// <summary>Segundo Cliente Delegante de demo, sin datos de usuario propios — ver DelegacionDemoSeeder.NombreTenantClienteDemo2.</summary>
    public const string NombreClienteDelegadoDemo2 = "Transportes Planet Express S.A. (Cliente Delegante demo 2)";

    /// <summary>
    /// Nombre del tenant de origen del Administrador inicial (la Consultora,
    /// ADR-004 § 5.1) — mismo criterio de duplicación que
    /// NombreClienteDelegadoDemo: TenantSeedData vive en Infrastructure, que
    /// este proyecto de test no referencia.
    /// </summary>
    public const string NombreTenantOrigenPorDefecto = "Organización principal";

    /// <summary>
    /// La Consultora de la demo (ArcoSPA, no TALVEG — decisión del
    /// propietario del 2026-08-14: la cuenta de plataforma no debe operar
    /// ningún Delegated Workspace) y su Administrador propio, con el mismo
    /// 2FA fijo que el resto de la siembra (ver DelegacionDemoSeeder).
    /// </summary>
    public const string NombreTenantConsultora = "ArcoSPA Prevención S.L. (Consultora demo)";
    public const string EmailAdministradorConsultora = "admin.arcospa@caemanager.local";

    /// <summary>
    /// Operador Delegado de ArcoSPA con rol Consulta (ver
    /// DelegacionDemoSeeder.SembrarOperadoresConsultoraAsync), delegado sobre
    /// <see cref="NombreClienteDelegadoDemo"/> (Dexter) — el mismo workspace que
    /// el Administrador inicial, pero con otro rol. A diferencia de ese
    /// Administrador (rol GestorCae dentro del workspace delegado, HO-136-05:
    /// cero AsignacionCartera, alcance cero por diseño, ver AlcanceRolesTests),
    /// Consulta tiene <c>TieneAccesoTotalAsync</c> sin depender de cartera
    /// (AlcanceDatosService), así que SÍ ve las Empresas sembradas al entrar a
    /// ese workspace. Usarlo cuando el test necesite comprobar contenido
    /// visible tras un cambio de workspace sin que el resultado dependa de si
    /// alguien le asignó cartera.
    /// </summary>
    public const string EmailOperadorConsultaConsultora = "prueba.operador.consulta1@caemanager.local";

    /// <summary>Primer Cliente Delegante de la demo — la referencia de "empresa final" (ver DelegacionDemoSeeder.NombreTenantRefrielectric).</summary>
    public const string NombreTenantRefrielectric = "Refrielectric S.L. (Cliente Delegante demo)";

    public static string EmailPrueba(string rolEnMinusculas, int numero) =>
        $"prueba.{rolEnMinusculas}{numero}@caemanager.local";

    /// <summary>
    /// Cambia el "Cliente activo" (ver SelectorClienteActivo.razor) al
    /// Cliente Delegante indicado por nombre, usando el &lt;select&gt; real de
    /// la interfaz.
    ///
    /// Antes esto navegaba a mano al endpoint, saltándose el selector: el
    /// cambio lo disparaba un @onchange de Blazor que exigía tener el circuito
    /// ya interactivo (ida y vuelta por SignalR) y resultó intermitente — a
    /// veces el evento no llegaba a dispararse desde Playwright y el cliente
    /// activo no cambiaba sin dar ningún error visible. Desde el arreglo de
    /// M-8 el selector es un &lt;form&gt; HTML que hace POST, así que el envío
    /// lo hace el navegador sin depender de SignalR y se puede ejercitar la
    /// interfaz de verdad — que además es lo que hace el usuario.
    ///
    /// HO-136-05: <c>SelectOptionAsync</c> marca el &lt;option&gt; como
    /// seleccionado en el DOM del cliente antes de que el formulario llegue a
    /// enviarse — eso es obra de Playwright, no del servidor. Un caller que
    /// comprobara solo <c>option:checked</c> justo después daría por hecho el
    /// cambio de workspace aunque el POST nunca hubiera llegado a
    /// <c>/cuenta/cliente-activo</c> (o el servidor lo hubiera rechazado con
    /// 401/403): esa comprobación no distingue "el cliente marcó la opción"
    /// de "el servidor aplicó el cambio". La única prueba de que el servidor
    /// autorizó y escribió/borró la cookie es su propia respuesta HTTP.
    ///
    /// Medido por mutación (HO-136-05): un 3xx por sí solo NO basta. Bajo
    /// cookie authentication (<c>ConfigureApplicationCookie</c> en Program.cs)
    /// <c>Results.Forbid()</c>/<c>Results.Unauthorized()</c> no llegan al
    /// navegador como 401/403 — el middleware los convierte en un 302 hacia
    /// <c>LoginPath</c>/<c>AccessDeniedPath</c>, que sigue siendo un 3xx.
    /// Forzar el endpoint a denegar siempre (mutación de prueba) lo confirmó:
    /// el status seguía en rango 3xx y este assert no lo detectaba — el fallo
    /// solo aparecía dos pasos más tarde, en el <c>&lt;select&gt;</c>, con un
    /// mensaje que no apuntaba a la causa real. Por eso además de un 3xx se
    /// exige que el redirect NO aterrice en ninguna de esas dos rutas de
    /// autenticación/autorización.
    /// </summary>
    public static async Task CambiarClienteActivoAsync(IPage page, string baseUrl, string nombreCliente)
    {
        var opcion = page.Locator(".selector-cliente-activo option", new PageLocatorOptions { HasText = nombreCliente });
        var tenantId = await opcion.GetAttributeAsync("value");

        var respuestaCambio = await page.RunAndWaitForResponseAsync(
            () => page.SelectOptionAsync(".selector-cliente-activo", new SelectOptionValue { Value = tenantId }),
            respuesta => respuesta.Url.Contains("/cuenta/cliente-activo"));

        Assert.True(
            respuestaCambio.Status is >= 300 and < 400,
            $"POST a /cuenta/cliente-activo devolvió {respuestaCambio.Status} (se esperaba una redirección 3xx) " +
            $"al intentar cambiar a «{nombreCliente}» — el servidor no aplicó el cambio de workspace, así que " +
            "cualquier comprobación posterior sobre el <select> del cliente estaría midiendo una marca sin efecto.");

        var destinoRedirect = respuestaCambio.Headers.GetValueOrDefault("location") ?? string.Empty;
        Assert.False(
            destinoRedirect.Contains("acceso-denegado") || destinoRedirect.Contains("iniciar-sesion"),
            $"POST a /cuenta/cliente-activo redirigió a «{destinoRedirect}» al intentar cambiar a «{nombreCliente}» " +
            "— eso es LoginPath/AccessDeniedPath (ConfigureApplicationCookie en Program.cs), no un cambio de " +
            "workspace aplicado: Results.Forbid()/Unauthorized() llegan al navegador como un 3xx hacia esa ruta, " +
            "no como 401/403, así que el status por sí solo no bastaba para confirmar que el servidor autorizó el " +
            "cambio.");

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Descarta el modal de notificaciones pendientes (ver
    /// Features/Notificaciones/NotificacionesPopup.razor, montado en
    /// MainLayout) si aparece — se dispara en el primer render de cada
    /// circuito nuevo (recarga real de página) mientras el usuario tenga
    /// notificaciones sin leer, y bloquea toda interacción con la página
    /// (<c>CerrarAlHacerClicFuera="false"</c>) hasta que se descarta. Los
    /// usuarios <c>prueba.&lt;rol&gt;</c> de DatosPruebaSeeder arrancan con una
    /// notificación sin leer a propósito ("la campana no debe arrancar
    /// vacía") — sin este paso, cualquier test que inicie sesión con esos
    /// usuarios y luego interactúe con la página se bloquea contra el modal.
    /// No-op si no hay ninguna pendiente.
    /// </summary>
    public static async Task DescartarNotificacionesPendientesAsync(IPage page)
    {
        // Como mucho unas pocas notificaciones sembradas por usuario — el
        // límite evita un bucle infinito si el modal nunca llega a cerrarse.
        for (var intentos = 0; intentos < 8; intentos++)
        {
            var botonOmitir = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Omitir" });
            if (await botonOmitir.CountAsync() == 0) return;

            try
            {
                // Timeout corto y locator fresco en cada vuelta: justo tras el
                // login la página puede estar a mitad de la transición de
                // prerenderizado estático a circuito interactivo, y el DOM del
                // modal se sustituye entero en ese momento — un clic que cae
                // justo ahí ve el elemento "detached" y hay que reintentarlo
                // contra el nuevo DOM, no contra la misma referencia.
                await botonOmitir.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
            }

            await page.WaitForTimeoutAsync(300);
        }
    }

    /// <summary>
    /// Abre el desplegable "⋯" de <c>MenuAcciones.razor</c> a partir de su
    /// botón disparador y devuelve el panel ya abierto, listo para localizar
    /// dentro de él la acción que toque.
    ///
    /// El flake crónico del job E2E (siempre "Timeout 30000ms exceeded"
    /// esperando algo dentro de <c>.menu-acciones-panel</c>, cambiando de test
    /// entre ejecuciones del mismo commit) sale de que abrir el menú depende
    /// de un <c>@onclick</c> server-side: MenuAcciones vive en páginas
    /// <c>@rendermode InteractiveServer</c>, que se prerenderizan estáticas
    /// primero. El botón está en el DOM —visible, habilitado y perfectamente
    /// clicable— desde ese prerenderizado, pero su controlador no existe hasta
    /// que el componente se vuelve interactivo por el circuito; y un re-render
    /// simultáneo (QuickGrid reconstruyendo la fila tras refiltrar) puede
    /// invalidar el id del controlador de un clic ya en vuelo. En ambos casos
    /// el clic se pierde EN SILENCIO: Playwright lo da por entregado, el panel
    /// nunca llega a abrirse, y el fallo aparece 30s después en la espera
    /// siguiente. Por eso el arreglo no es subir el timeout — el clic no llega
    /// tarde, no llega.
    ///
    /// La única señal fiable de que el clic sí llegó al circuito es el
    /// <c>aria-expanded</c> del propio disparador, que Blazor renderiza desde
    /// <c>_abierto</c>. Y como <c>Alternar()</c> es un interruptor, reintentar
    /// a ciegas es peor que no reintentar: un segundo clic sobre un menú que
    /// SÍ había abierto lo vuelve a cerrar, y la espera posterior se come los
    /// 30s enteros contra un panel cerrado. De ahí las dos reglas de este
    /// helper: solo se clica tras confirmar que el menú sigue cerrado, y el
    /// método es idempotente (si ya está abierto, no lo toca).
    ///
    /// <paramref name="disparador"/> debe resolver a un único botón
    /// <c>.menu-acciones-disparador</c>; el panel se busca dentro del mismo
    /// <c>.menu-acciones</c> que ese botón, no en toda la página, así que
    /// nunca se confunde con el de otra fila.
    /// </summary>
    public static async Task<ILocator> AbrirMenuAccionesAsync(ILocator disparador)
    {
        var panel = disparador.Locator("xpath=..").Locator(".menu-acciones-panel");

        // El disparador tiene que existir y ser clicable antes de contar
        // intentos: si todavía no está en pantalla, el problema es otro y
        // debe reportarse como tal, no gastarse los reintentos.
        await disparador.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        for (var intento = 1; intento <= IntentosAbrirMenuAcciones; intento++)
        {
            if (await MenuAccionesSigueCerradoAsync(disparador))
                await disparador.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

            // 5s es enorme para una ida y vuelta de SignalR contra la app en
            // el mismo runner — si aria-expanded no ha cambiado en ese
            // tiempo, el clic no llegó al circuito y hay que repetirlo.
            if (!await EsperarMenuAccionesAbiertoAsync(disparador, TimeSpan.FromSeconds(5)))
                continue;

            // A partir de aquí el servidor ya sabe que el menú está abierto,
            // así que el panel es cuestión de que Blazor termine de parchear
            // el DOM — esperarlo (y no volver a clicar) es lo correcto.
            await panel.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            return panel;
        }

        throw new TimeoutException(
            $"El menú \"⋯\" (MenuAcciones.razor) no llegó a abrirse tras {IntentosAbrirMenuAcciones} clics: " +
            "aria-expanded se quedó en \"false\" cada vez, así que ningún clic llegó al circuito de Blazor " +
            "(componente aún no interactivo tras el prerenderizado, o controlador @onclick invalidado por un " +
            "re-render simultáneo).");
    }

    private const int IntentosAbrirMenuAcciones = 4;

    private const int IntentosPulsarAccionDeMenu = 3;

    /// <summary>
    /// Abre el menú "⋯" de <see cref="AbrirMenuAccionesAsync"/> y pulsa uno de
    /// sus ítems por su texto, reabriendo el menú desde cero y reintentando si
    /// el clic llega a un elemento que se desprende (o nunca se estabiliza) a
    /// mitad de la acción.
    ///
    /// <para><b>El fallo que motiva este helper</b> (PR #574, run 34624659728,
    /// job "Tests E2E (Playwright)", <c>FlujoCicloDocumentalTests…Renovar</c>):
    /// "Timeout 30000ms exceeded […] waiting for element to be visible, enabled
    /// and stable" sobre el <c>&lt;button role="menuitem"&gt;</c> de "Renovar",
    /// con <see cref="AbrirMenuAccionesAsync"/> ya habiendo confirmado
    /// <c>aria-expanded="true"</c> justo antes. O sea: el menú SÍ se abrió,
    /// pero el ítem se desprendió entre que Playwright lo resolvió y el clic
    /// llegó a ejecutarse — un re-render reconstruyó la fila (y con ella la
    /// instancia de <c>MenuAcciones</c>/<c>ItemMenuAccion</c>, ver esos
    /// archivos en Components/DesignSystem) con el panel todavía abierto.
    /// </para>
    ///
    /// <para><b>Investigado el lado de producto antes de asumir que es solo
    /// arnés</b> (regla de Diagnóstico de fallos): ni <c>MenuAcciones.razor</c>
    /// ni <c>ItemMenuAccion.razor</c> traen temporizador ni suscripción propia
    /// que provoque ese re-render — el candidato estaba en el consumidor. En
    /// <c>Documentos.razor.cs</c>, <c>BuscarAsync</c> → <c>RecargarAsync</c> →
    /// <c>_grid.RefreshDataAsync()</c> reconstruye la QuickGrid entera cada vez
    /// que <c>CampoTexto</c> notifica <c>ValorChanged</c> — y
    /// <c>CampoTexto.ManejarBlurAsync</c> (ver ese archivo) reinvoca
    /// <c>ValorChanged</c> en CADA blur sin comprobar si el valor ya se había
    /// notificado por el debounce (a diferencia de <c>ManejarCambioAsync</c>,
    /// que sí evita notificar dos veces lo mismo): un defecto de producto real
    /// y de alcance amplio (~45 pantallas usan <c>CampoTexto</c> como filtro de
    /// lista), que se reporta por separado — no se parchea aquí.
    /// </para>
    ///
    /// <para><b>Esa hipótesis concreta quedó refutada por mutación</b> (regla
    /// de Validación del instrumento: "una mutación que pasa cuando predijiste
    /// rojo es un hallazgo, no un contratiempo"): forzar exactamente ese
    /// mecanismo —blur del buscador tras clicar "Más acciones", recarga
    /// redundante de la rejilla a mitad del clic sobre "Renovar"— NO reprodujo
    /// ningún fallo. QuickGrid renderiza sin <c>@key</c> por fila, y para el
    /// MISMO conjunto de resultados (misma búsqueda) el árbol de render tiene
    /// la misma forma en la misma posición, así que Blazor parchea la fila en
    /// sitio en vez de destruir y recrear la instancia de <c>MenuAcciones</c>
    /// — <c>_abierto</c> sobrevive intacto a la recarga redundante. Lo que SÍ
    /// reproduce el fallo, medido por mutación: quitar el panel del DOM justo
    /// cuando aparece (vía <c>MutationObserver</c> en el propio test, sin
    /// tocar código de producto) — simula que un
    /// re-render genuino se lleva por delante el panel recién abierto antes de
    /// que el clic llegue, dejando <c>_abierto</c> desincronizado del cliente
    /// (Menu?.Cerrar() del lado servidor, o cualquier mecanismo que fuerce una
    /// instancia nueva del componente). Con <c>IntentosPulsarAccionDeMenu</c> a
    /// 1 esto da rojo por "Timeout … waiting for … .menu-acciones-panel to be
    /// visible" — mismo tipo de fallo por actionability que el de CI, aunque no
    /// idéntico en el punto exacto—; con los 3 intentos de vuelta, verde. Esa
    /// misma mutación destapó además un bug real en este propio helper (ver el
    /// comentario en el cuerpo del método, más abajo) que ya está corregido.
    /// </para>
    ///
    /// <para>Reabrir y reintentar es seguro: si el re-render cerró el panel,
    /// <see cref="AbrirMenuAccionesAsync"/> lo reabre (y es idempotente si
    /// seguía abierto); y el efecto que se comprueba al final no es "el clic
    /// se entregó" sino que <c>ItemMenuAccion.EjecutarAsync</c> invocó
    /// <c>OnClick</c> con éxito y cerró el menú — <c>Menu?.Cerrar()</c> corre
    /// SOLO después de que el callback del servidor termine, así que el panel
    /// desaparecido del DOM es la señal de que la acción se ejecutó de
    /// verdad, no un simulacro de Playwright.</para>
    /// </summary>
    public static async Task PulsarAccionDeMenuAsync(ILocator disparador, string textoAccion)
    {
        for (var intento = 1; intento <= IntentosPulsarAccionDeMenu; intento++)
        {
            try
            {
                // AbrirMenuAccionesAsync va DENTRO del mismo try que el clic
                // — no solo este último. Medido por mutación: con solo el
                // clic protegido, un re-render que se lleva el panel justo
                // tras confirmarse aria-expanded (antes de que el propio
                // AbrirMenuAccionesAsync termine de esperarlo) lanza su
                // TimeoutException sin pasar por ningún catch de este bucle, y
                // ese fallo escapa de PulsarAccionDeMenuAsync entero sin
                // agotar IntentosPulsarAccionDeMenu — el reintento de más
                // arriba nunca llegaba a ejecutarse.
                var panel = await AbrirMenuAccionesAsync(disparador);
                var item = panel.GetByText(textoAccion, new LocatorGetByTextOptions { Exact = true });
                await item.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });

                // Menu?.Cerrar() en ItemMenuAccion.EjecutarAsync solo corre
                // tras invocar OnClick con éxito — que el panel desaparezca
                // del DOM es la única señal de que la acción llegó a
                // ejecutarse de verdad, no solo que el clic se entregó.
                if (await EsperarMenuAccionesCerradoAsync(panel, TimeSpan.FromSeconds(5)))
                    return;
            }
            catch (TimeoutException)
            {
                // Microsoft.Playwright NO define su propio TimeoutException
                // (confirmado por reflexión sobre el ensamblado: solo existe
                // Microsoft.Playwright.PlaywrightException, que deriva de
                // System.Exception) — WaitForAsync/ClickAsync con Timeout
                // lanzan System.TimeoutException de verdad al agotar su
                // reintento interno de actionability, así que ese es el tipo
                // correcto a capturar aquí (mismo criterio que
                // DescartarNotificacionesPendientesAsync, más arriba en este
                // fichero). Un catch(PlaywrightException) NUNCA lo atrapa —
                // medido en CI (PR #580, run 34640688258, cola de fusión):
                // ese catch dejó escapar la excepción cruda 3 veces seguidas
                // sin que el retry llegara a ejecutarse ni una vez, pese a
                // estar bien posicionado dentro del try.
                //
                // El menú no llegó a abrirse de forma estable, o el ítem se
                // desprendió (o nunca se estabilizó) a mitad del clic — un
                // re-render se llevó el panel por delante. Se reabre desde
                // cero en la siguiente vuelta en vez de reintentar sobre el
                // mismo locator: tras un re-render el elemento ya resuelto no
                // es fiable.
            }
        }

        throw new TimeoutException(
            $"La acción \"{textoAccion}\" del menú \"⋯\" no llegó a ejecutarse tras {IntentosPulsarAccionDeMenu} " +
            "intentos: o el clic nunca llegó a entregarse de forma estable, o el panel nunca se cerró después " +
            "(Menu?.Cerrar() no corrió, así que OnClick tampoco terminó con éxito).");
    }

    private static async Task<bool> EsperarMenuAccionesCerradoAsync(ILocator panel, TimeSpan limite)
    {
        var vencimiento = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < vencimiento)
        {
            if (await panel.CountAsync() == 0) return true;
            await Task.Delay(100);
        }

        return false;
    }

    private const int IntentosSeleccionarPestana = 4;

    /// <summary>
    /// Selecciona una pestaña de <c>Pestanas.razor</c> confirmando que el clic
    /// llegó de verdad al circuito, y devuelve su locator ya activo.
    ///
    /// Mismo modo de fallo que <see cref="AbrirMenuAccionesAsync"/>, y por la
    /// misma razón: el botón <c>role="tab"</c> lleva un <c>@onclick</c>
    /// server-side (<c>ActivarAsync</c>), y las páginas que montan este
    /// componente son <c>@rendermode InteractiveServer</c>, o sea que se
    /// prerenderizan estáticas primero. Desde ese prerenderizado el botón está
    /// en el DOM —visible, habilitado y clicable— pero su controlador no existe
    /// hasta que el componente se vuelve interactivo por el circuito. Un clic
    /// que cae en esa ventana se pierde EN SILENCIO: Playwright lo da por
    /// entregado, la pestaña nunca se activa, y el fallo aparece 30 s después
    /// en la espera siguiente.
    ///
    /// Medido en CI (run 33649091982, intento 1, sobre 01aa56ba):
    /// <c>PestanaUrlDurableTests…carga_en_frio</c> falló con
    /// "Timeout 30000ms exceeded" y el registro de Playwright
    /// "waiting for navigation to …/documentos?Pestana=revision-ia until Load"
    /// en el <c>WaitForURLAsync</c> posterior al clic — es decir, el clic no
    /// llegó tarde: no llegó. El mismo test pasó en 2 s en el intento 2. Por
    /// eso el arreglo no es subir el timeout ni esperar más tiempo: es esperar
    /// una <b>señal</b> de que el clic sí llegó, y repetirlo si no llegó.
    ///
    /// La señal es <c>aria-selected</c>, que <c>Pestanas.razor</c> renderiza
    /// desde <c>PestanaActiva</c> — estado del servidor, no del navegador. Y al
    /// contrario que el menú "⋯", aquí reintentar a ciegas es seguro:
    /// <c>ActivarAsync</c> es idempotente (<c>id == PestanaActiva</c> no hace
    /// nada), no un interruptor, así que un segundo clic sobre una pestaña ya
    /// activa no la desactiva. De ahí que este helper no necesite la
    /// comprobación previa de "sigue cerrado" que sí necesita aquel.
    ///
    /// <b>Cómo reproducir el fallo a voluntad</b> (la carrera no se manifiesta
    /// en una máquina de desarrollo, que arranca el circuito antes de que dé
    /// tiempo a clicar — "pasa en local" no refuta un rojo de CI): retrasar el
    /// script que arranca el circuito y navegar sin esperarlo,
    /// <c>page.RouteAsync("**/blazor.web*.js", …Task.Delay(6000)…)</c> más
    /// <c>GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Commit })</c>.
    /// Con este helper el test pasa (reintenta hasta que el circuito responde);
    /// sustituyéndolo por un solo <c>ClickAsync</c> falla con exactamente el
    /// mismo texto que en CI — "waiting for navigation to … until Load" en
    /// <c>TaskHelper.WithTimeout</c>.
    /// </summary>
    public static async Task<ILocator> SeleccionarPestanaAsync(IPage page, ILocator pestana, string nombreParaElError)
    {
        await pestana.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        for (var intento = 1; intento <= IntentosSeleccionarPestana; intento++)
        {
            if (await pestana.GetAttributeAsync("aria-selected") == "true")
                return pestana;

            await pestana.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

            // 5 s es enorme para una ida y vuelta de SignalR contra la app en el
            // mismo runner — si aria-selected no ha cambiado en ese tiempo, el
            // clic no llegó al circuito y hay que repetirlo.
            if (await EsperarPestanaActivaAsync(pestana, TimeSpan.FromSeconds(5)))
                return pestana;
        }

        throw new TimeoutException(
            $"La pestaña \"{nombreParaElError}\" (Pestanas.razor) no llegó a activarse tras " +
            $"{IntentosSeleccionarPestana} clics: aria-selected se quedó en \"false\" cada vez, así que ningún " +
            "clic llegó al circuito de Blazor (componente aún no interactivo tras el prerenderizado). " +
            $"URL en ese momento: {page.Url}");
    }

    private static async Task<bool> EsperarPestanaActivaAsync(ILocator pestana, TimeSpan limite)
    {
        var vencimiento = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < vencimiento)
        {
            if (await pestana.GetAttributeAsync("aria-selected") == "true")
                return true;

            await Task.Delay(100);
        }

        return false;
    }


    /// <summary>
    /// Confirma que el menú sigue cerrado antes de (re)clicar. Lee
    /// <c>aria-expanded</c> dos veces separadas por un margen holgado frente a
    /// una ida y vuelta de SignalR: así un clic anterior que estuviera llegando
    /// justo en ese instante se ve aquí, en vez de que el clic nuevo lo anule
    /// cerrando un menú recién abierto.
    /// </summary>
    private static async Task<bool> MenuAccionesSigueCerradoAsync(ILocator disparador)
    {
        if (await disparador.GetAttributeAsync("aria-expanded") == "true")
            return false;

        await Task.Delay(250);
        return await disparador.GetAttributeAsync("aria-expanded") != "true";
    }

    private static async Task<bool> EsperarMenuAccionesAbiertoAsync(ILocator disparador, TimeSpan limite)
    {
        var vencimiento = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < vencimiento)
        {
            if (await disparador.GetAttributeAsync("aria-expanded") == "true")
                return true;

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>
    /// Selecciona una fila de la bandeja unificada de <c>/comunicaciones</c>
    /// (<c>FilaConversacion.razor</c>) confirmando que el clic llegó de verdad
    /// al circuito de Blazor antes de devolver el control.
    ///
    /// Mismo modo de fallo que <see cref="AbrirMenuAccionesAsync"/> y por el
    /// mismo motivo: la fila es un <c>&lt;li&gt;</c> con <c>@onclick</c> dentro
    /// de una página <c>@rendermode InteractiveServer</c>, así que está en el
    /// DOM —visible, clicable— desde el prerenderizado estático, antes de que
    /// su controlador exista del lado servidor; y un re-render simultáneo (la
    /// carga del detalle del hilo anterior, o el refresco en tiempo real de
    /// <c>AlRecibirMensajeAsync</c>) puede invalidar el id del controlador de
    /// un clic ya en vuelo. En los dos casos el clic se pierde EN SILENCIO:
    /// Playwright lo da por entregado, la selección nunca ocurre, y el fallo
    /// aparece 30 s después en la espera siguiente — que en
    /// <c>DeepLinksTests</c> era un <c>WaitForURLAsync</c> y se comía el
    /// timeout entero sin decir por qué (job 99116263442, "Tests E2E
    /// (Playwright)" de main <c>eed3a648</c>: 33 s de test para 3 s de
    /// trabajo real). Por eso el arreglo no es subir el timeout — el clic no
    /// llega tarde, no llega.
    ///
    /// La señal fiable de que sí llegó es la clase <c>bandeja-fila-activa</c>,
    /// que Blazor renderiza desde <c>_conversacionSeleccionadaId</c> (ver
    /// Bandeja.razor): estado del servidor, no del navegador. A diferencia del
    /// menú "⋯", seleccionar no es un interruptor sino una asignación, así que
    /// reintentar es idempotente y no puede deshacer un clic que sí había
    /// llegado; aun así solo se reclica si la fila sigue sin estar activa.
    ///
    /// <paramref name="fila"/> debe resolver a un único <c>.bandeja-fila</c>.
    /// </summary>
    public static async Task SeleccionarFilaBandejaAsync(ILocator fila)
    {
        await fila.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        for (var intento = 1; intento <= IntentosSeleccionarFilaBandeja; intento++)
        {
            if (!await FilaBandejaActivaAsync(fila))
                await fila.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

            // Mismo margen que AbrirMenuAccionesAsync: 5 s es enorme para una
            // ida y vuelta de SignalR contra la app en el mismo runner, así
            // que si la clase no ha aparecido el clic no llegó al circuito.
            if (await EsperarFilaBandejaActivaAsync(fila, TimeSpan.FromSeconds(5)))
                return;
        }

        throw new TimeoutException(
            $"La fila de la bandeja no llegó a seleccionarse tras {IntentosSeleccionarFilaBandeja} clics: " +
            "nunca apareció la clase \"bandeja-fila-activa\" que Blazor renderiza desde " +
            "_conversacionSeleccionadaId (ver Bandeja.razor), así que ningún clic llegó al circuito " +
            "(componente aún no interactivo tras el prerenderizado, o controlador @onclick invalidado por " +
            "un re-render simultáneo).");
    }

    private const int IntentosSeleccionarFilaBandeja = 4;

    /// <summary>
    /// <c>bandeja-fila-activa</c> no es prefijo ni sufijo de ninguna otra clase
    /// de la fila (<c>bandeja-fila</c>, <c>bandeja-fila-esperando</c>), así que
    /// buscarla como subcadena del atributo no puede dar un falso positivo.
    /// </summary>
    private static async Task<bool> FilaBandejaActivaAsync(ILocator fila) =>
        (await fila.GetAttributeAsync("class"))?.Contains("bandeja-fila-activa") == true;

    private static async Task<bool> EsperarFilaBandejaActivaAsync(ILocator fila, TimeSpan limite)
    {
        var vencimiento = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < vencimiento)
        {
            if (await FilaBandejaActivaAsync(fila))
                return true;

            await Task.Delay(100);
        }

        return false;
    }

    public static async Task IniciarSesionAsync(IPage page, string baseUrl, string email, string password)
    {
        await page.GotoAsync($"{baseUrl}/cuenta/iniciar-sesion");
        await page.FillAsync("#email", email);
        await page.FillAsync("#password", password);
        await page.ClickAsync("button[type=\"submit\"]");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Solo el Administrador inicial tiene 2FA activo hoy (P1-13 de
        // docs/business/MATURITY_REVIEW.md) — el resto de cuentas de prueba
        // pasan de largo por esta rama y siguen directas al dashboard.
        if (page.Url.Contains("/cuenta/verificar-2fa"))
        {
            await page.FillAsync("#codigo", GenerarCodigoTotp(ClaveTotpAdministrador));
            await page.ClickAsync("button[type=\"submit\"]");
        }

        await page.Locator(".nav-principal").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    /// <summary>
    /// TOTP de 6 dígitos (RFC 6238, HMAC-SHA1, paso de 30s) — el mismo
    /// algoritmo que <c>UserManager.VerifyTwoFactorTokenAsync</c> valida del
    /// lado servidor vía <c>AuthenticatorTokenProvider</c>. Sin paquete
    /// nuevo: es la única forma de que este proyecto de test calcule el
    /// código del Administrador inicial (ver ClaveTotpAdministrador) sin
    /// acceso a base de datos ni a Infrastructure.
    /// </summary>
    public static string GenerarCodigoTotp(string claveBase32, DateTimeOffset? momento = null)
    {
        var clave = DescodificarBase32(claveBase32);
        var contador = (long)(momento ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;

        var contadorBytes = BitConverter.GetBytes(contador);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(contadorBytes);

        using var hmac = new HMACSHA1(clave);
        var hash = hmac.ComputeHash(contadorBytes);

        var desplazamiento = hash[^1] & 0x0F;
        var codigoBinario =
            ((hash[desplazamiento] & 0x7F) << 24) |
            ((hash[desplazamiento + 1] & 0xFF) << 16) |
            ((hash[desplazamiento + 2] & 0xFF) << 8) |
            (hash[desplazamiento + 3] & 0xFF);

        return (codigoBinario % 1_000_000).ToString("D6");
    }

    private static byte[] DescodificarBase32(string base32)
    {
        const string alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bitsEnBuffer = 0;

        foreach (var caracter in base32.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alfabeto.IndexOf(caracter);
            bitsEnBuffer += 5;
            if (bitsEnBuffer < 8) continue;

            bitsEnBuffer -= 8;
            bytes.Add((byte)((buffer >> bitsEnBuffer) & 0xFF));
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// page.GotoAsync hace una navegación real de navegador (no un
    /// enrutado del lado cliente de Blazor) — cada llamada tira abajo el
    /// circuit de SignalR y lo reconecta desde cero. Si se interactúa
    /// (clic, fill…) justo después de GotoAsync sin esperar a que el
    /// circuit reconecte, el elemento ya está en el DOM (prerenderizado)
    /// pero su @onclick todavía no está cableado del lado servidor, así
    /// que el clic no hace nada — un timeout posterior en la siguiente
    /// espera, no un error inmediato. Esperar a "networkidle" (sin
    /// conexiones activas ~500ms, lo que cubre el handshake del
    /// WebSocket) es el mismo patrón ya usado con éxito en las
    /// verificaciones manuales previas de este proyecto.
    /// </summary>
    public static async Task NavegarYEsperarAsync(IPage page, string url)
    {
        await page.GotoAsync(url);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Resuelve el campo "Empresa" del drawer de alta de Trabajador
    /// (Trabajadores.razor), que renderiza de dos formas mutuamente
    /// excluyentes según el estado del tenant en ese instante: un
    /// combobox real cuando hay más de una Empresa, o un CampoInfo de
    /// solo lectura cuando DDL-076 (perfil Cliente Directo + una única
    /// Empresa) resuelve "en silencio" — ver _resolverEmpresaEnSilencio
    /// en Trabajadores.razor.cs. Cuál de los dos aparece depende del
    /// número de Empresas ya creadas por OTROS tests que comparten el
    /// mismo tenant en "AppCollection", así que no es fijo por test.
    ///
    /// Comprobar comboEmpresa.CountAsync() inmediatamente después de abrir
    /// el drawer es una carrera real (visto en CI): Blazor todavía no ha
    /// terminado de decidir/renderizar cuál de las dos ramas le toca, así
    /// que un CountAsync() prematuro puede leer "0" aunque el combobox
    /// esté a punto de aparecer, y el resto del test acaba esperando el
    /// campo equivocado. Se espera primero a que cualquiera de los dos
    /// esté realmente visible, y solo entonces se decide la rama.
    /// </summary>
    public static async Task SeleccionarEmpresaEnDrawerTrabajadorAsync(ILocator drawer, string razonSocialEmpresa)
    {
        var comboEmpresa = drawer.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Empresa" });
        var infoEmpresa = drawer.Locator(".campo-info-valor", new LocatorLocatorOptions { HasText = razonSocialEmpresa });

        await comboEmpresa.Or(infoEmpresa).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        if (await comboEmpresa.CountAsync() > 0)
        {
            await comboEmpresa.SelectOptionAsync(new SelectOptionValue { Label = razonSocialEmpresa });
        }
        else
        {
            await infoEmpresa.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        }
    }

    /// <summary>
    /// Genera un PDF de una página válido con PDFsharp — la misma librería
    /// que usa ConversorArchivosPdf en producción para combinar/leer los
    /// archivos subidos — para que el flujo de subida real (import vía
    /// PdfReader.Open) tenga un archivo que de verdad pueda parsear, en vez
    /// de unos bytes con cabecera "%PDF" pero sin estructura real. Página en
    /// blanco a propósito, sin texto: dibujar texto requiere un
    /// IFontResolver (ver EmbeddedFontResolver de CaeManager.Web, registrado
    /// en su propio Program.cs) que este proceso de test, al no arrancar esa
    /// app in-process, nunca tiene configurado — una página vacía sigue
    /// siendo un PDF perfectamente válido y parseable, y es lo único que
    /// hace falta para probar el flujo de subida.
    /// </summary>
    public static string GenerarPdfDePruebaEnDisco(string nombreArchivo = "documento-prueba.pdf")
    {
        using var documento = new PdfDocument();
        documento.AddPage();

        var ruta = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{nombreArchivo}");
        documento.Save(ruta);
        return ruta;
    }

    /// <summary>Mismo algoritmo que DatosPruebaSeeder.GenerarCifValido (ver ese archivo) — CIF sintético válido, letra 'B'.</summary>
    public static string GenerarCifValido(int numero)
    {
        var digitos = numero.ToString("D7");
        var sumaPares = 0;
        var sumaImpares = 0;
        for (var i = 0; i < digitos.Length; i++)
        {
            var num = digitos[i] - '0';
            if (i % 2 == 1)
            {
                sumaPares += num;
            }
            else
            {
                var multiplicado = num * 2;
                sumaImpares += multiplicado > 9 ? multiplicado - 9 : multiplicado;
            }
        }

        var residuo = (sumaPares + sumaImpares) % 10;
        var digitoControl = residuo == 0 ? 0 : 10 - residuo;
        return $"B{digitos}{digitoControl}";
    }

    /// <summary>Mismo algoritmo que DatosPruebaSeeder.GenerarDniValido — DNI sintético con dígito de control válido.</summary>
    public static string GenerarDniValido(int numero)
    {
        const string letrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";
        return $"{numero:D8}{letrasControl[numero % 23]}";
    }

    /// <summary>
    /// Vuelve inválido un CIF generado por <see cref="GenerarCifValido"/> sin
    /// tocar su formato (letra + 7 dígitos + dígito de control) — solo
    /// cambia el dígito de control por uno distinto, así que
    /// ValidadorIdentificacion.Analizar lo sigue reconociendo como
    /// TipoIdentificacion.NifEmpresa pero con EsValido=false. Para los tests
    /// de importación que deliberadamente prueban la fila "CIF no válido".
    /// </summary>
    public static string InvalidarCif(string cifValido)
    {
        var ultimoDigito = cifValido[^1];
        var sustituto = ultimoDigito == '0' ? '1' : '0';
        return cifValido[..^1] + sustituto;
    }

    /// <summary>
    /// Guarda un libro ClosedXML ya construido por el test en un archivo
    /// temporal — mismo patrón que GenerarPdfDePruebaEnDisco (SetInputFilesAsync
    /// necesita una ruta real en disco). ClosedXML es la misma librería que
    /// ya usan ClosedXmlPlantillaClientesService/ClosedXmlPlantillaCombinadaService/
    /// ClosedXmlPlantillaDocumentosService/ClosedXmlImportacionParser en
    /// producción para generar y leer estos mismos formatos — cada test
    /// construye el libro con las columnas exactas que ese parser espera
    /// (documentadas en cada uno de esos archivos), no una plantilla
    /// genérica de conveniencia.
    /// </summary>
    public static string GuardarLibroDePruebaEnDisco(XLWorkbook libro, string nombreArchivo)
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{nombreArchivo}");
        libro.SaveAs(ruta);
        return ruta;
    }

    private const int IntentosSubirArchivoImportacion = 2;

    /// <summary>
    /// Sube el archivo al &lt;InputFile&gt; del asistente de importación
    /// (Importacion.razor, paso 2) y espera una <b>señal del servidor</b> de que
    /// el análisis arrancó, repitiendo la subida si no llega.
    ///
    /// <para><b>Por qué no vale esperar a que se habilite el botón.</b> "Ver
    /// plan de importación" se pinta con <c>Deshabilitado="!TienePlan"</c>, y
    /// <c>TienePlan</c> solo se pone a true en la rama de éxito de
    /// <c>ManejarArchivoSeleccionadoAsync</c>. En todas las demás ramas
    /// —excepción al leer el libro, archivo por encima del tamaño máximo, o un
    /// evento <c>change</c> que nunca llega al circuito— el botón se queda
    /// deshabilitado <b>para siempre</b>. Un <c>ToBeEnabledAsync</c> con timeout
    /// fijo no distingue "el análisis va lento" de "el análisis no va a terminar
    /// nunca"; como la mayoría de los estados alcanzables son permanentes,
    /// subir el timeout no arregla nada: tarda más en dar el mismo error ciego.
    /// </para>
    ///
    /// <para><b>Medido en CI</b> (cola de merge, run 34373414188, job
    /// 102540034790, sobre f6572fb4 — el mismo contenido pasó en la rama y dos
    /// veces en local). <c>ImportarClientesTests</c> falló con "Locator expected
    /// to be enabled … unexpected value disabled" tras 15 s, y
    /// "34 × locator resolved to &lt;button disabled&gt;". El sink de fichero de
    /// Serilog del propio job (artefacto <c>logs-caemanager-web-e2e</c>) sitúa
    /// ese circuito así: en <c>16:00:12.379</c> se pide
    /// <c>/js/zona-soltar-archivo.js</c> —o sea, el paso 2 SÍ renderizó y
    /// <c>ZonaSoltarArchivo.OnAfterRenderAsync</c> corrió— y desde ahí el
    /// circuito no vuelve a registrar <b>nada</b> hasta cerrarse en
    /// <c>16:00:27.391</c> ("/_blazor responded 101 in 15637 ms"). Quince
    /// segundos de silencio absoluto.</para>
    ///
    /// <para>Eso descarta por medición —no por argumento— la hipótesis por
    /// defecto de que "15 s no bastan con la máquina cargada":
    /// <c>LoggingBehavior</c> registra a Warning todo request que pase de
    /// <c>UmbralLentitudMs = 1000</c>, lecturas incluidas, y ese nivel sí sale
    /// en este fichero (lo demuestra el "CrearClienteCommand falló con
    /// Autorizacion.SoloLectura" del mismo log). En las 39 pruebas del run
    /// entero <b>no hay una sola línea de request lento</b>. Y los dos tests
    /// hermanos con esta misma forma corrieron segundos antes en la misma
    /// máquina: de renderizar la zona de soltar a tener la importación ya
    /// ejecutada tardaron 331 ms (combinada) y 1,30 s (documentos). El análisis
    /// no fue lento: no llegó a ocurrir.</para>
    ///
    /// <para><b>El arreglo</b> es el que ya usa
    /// <see cref="SeleccionarPestanaAsync"/> para el clic que se pierde en
    /// silencio: esperar una <b>señal</b> de que el evento llegó al servidor, y
    /// repetirlo si no llegó. La señal es que aparezca cualquiera de los tres
    /// observables del estado del servidor en el paso 2 —la barra de
    /// <c>ProgresoConMensajes</c> (<c>_analizando</c>), la alerta de error
    /// (<c>_mensajeError</c>) o el botón ya habilitado (<c>TienePlan</c>)—.
    /// Reintentar es seguro: <c>ManejarArchivoSeleccionadoAsync</c> es
    /// idempotente, empieza poniendo el plan a null y vuelve a analizar.</para>
    ///
    /// <para><b>Cómo reproducir el fallo a voluntad</b> (no se manifiesta en una
    /// máquina de desarrollo, igual que el de
    /// <see cref="SeleccionarPestanaAsync"/>): sustituir el
    /// &lt;input type=file&gt; por un clon justo antes de subir, con
    /// <c>page.EvalOnSelectorAsync("input[type=file]", "e =&gt; e.replaceWith(e.cloneNode(true))")</c>.
    /// El clon está en el DOM y acepta <c>SetInputFilesAsync</c>, pero perdió el
    /// cableado de Blazor, así que el <c>change</c> no llega al circuito —
    /// exactamente el estado observado en CI.</para>
    /// </summary>
    public static async Task SubirArchivoDeImportacionAsync(IPage page, string rutaArchivo)
    {
        var entrada = page.Locator("input[type=\"file\"]");

        for (var intento = 1; intento <= IntentosSubirArchivoImportacion; intento++)
        {
            await entrada.SetInputFilesAsync(rutaArchivo);

            // 5 s es enormísimo para que el servidor acuse recibo del change:
            // los dos tests hermanos hacen el ciclo entero (análisis, plan,
            // confirmación e importación escrita) en 331 ms y 1,30 s. Mismo
            // criterio y mismo número que SeleccionarPestanaAsync.
            if (await EsperarAcuseDeReciboDelAnalisisAsync(page, TimeSpan.FromSeconds(5)))
                return;
        }

        throw new TimeoutException(
            $"El análisis de la importación nunca arrancó tras {IntentosSubirArchivoImportacion} intentos de subir " +
            $"\"{Path.GetFileName(rutaArchivo)}\": ni barra de progreso (_analizando), ni alerta de error " +
            "(_mensajeError), ni plan (TienePlan). El evento 'change' del <InputFile> no llegó al circuito de " +
            $"Blazor. URL en ese momento: {page.Url}{await DescribirToastsAsync(page)}");
    }

    /// <summary>
    /// Espera al <b>desenlace</b> del análisis y lo nombra al fallar. Sustituye
    /// a un <c>Expect(boton).ToBeEnabledAsync(…)</c> a secas: si el análisis
    /// terminó en error, el mensaje del test es el del error y no un timeout
    /// ciego; y si no terminó, dice si seguía en curso o si la página estaba
    /// inerte. Ver <see cref="SubirArchivoDeImportacionAsync"/> para la medición
    /// que motiva las dos esperas.
    /// </summary>
    public static async Task EsperarPlanDeImportacionAsync(IPage page, int timeoutMs = 15_000)
    {
        try
        {
            await page.WaitForFunctionAsync(
                $"() => {{ const e = ({GuionEstadoDelAnalisis})(); return e === 'plan' || e === 'error'; }}",
                null,
                new PageWaitForFunctionOptions { Timeout = timeoutMs });
        }
        catch (PlaywrightException)
        {
            var estado = await LeerEstadoDelAnalisisAsync(page);
            throw new TimeoutException(
                $"El análisis de la importación no llegó a ningún desenlace en {timeoutMs} ms. Estado observado en " +
                $"la página: \"{estado}\" — \"analizando\" = sigue en curso de verdad (ahí sí faltaría tiempo); " +
                "\"inerte\" = ni progreso, ni error, ni plan, o sea que el 'change' se perdió y esperar más no " +
                $"habría servido de nada. URL: {page.Url}{await DescribirToastsAsync(page)}");
        }

        var mensajeError = await LeerAlertaDeFormularioAsync(page);
        if (mensajeError is not null)
            throw new InvalidOperationException(
                $"El análisis de la importación terminó en error, no en plan: \"{mensajeError}\" " +
                "(Importacion.razor, _mensajeError). Esto NO es un timeout: el servidor respondió.");
    }

    /// <summary>
    /// Los tres observables del estado del servidor durante el paso 2 de
    /// Importacion.razor, leídos del DOM en una sola pasada. Guion compartido
    /// por la espera y por el diagnóstico, para que ambos midan exactamente lo
    /// mismo.
    /// </summary>
    private const string GuionEstadoDelAnalisis = """
        () => {
            const boton = [...document.querySelectorAll('button')]
                .find(b => b.textContent.includes('Ver plan de importación'));
            return boton && !boton.disabled ? 'plan'
                : document.querySelector('.alerta-formulario') ? 'error'
                : document.querySelector('.progreso-carga') ? 'analizando'
                : null;
        }
        """;

    private static async Task<bool> EsperarAcuseDeReciboDelAnalisisAsync(IPage page, TimeSpan limite)
    {
        var vencimiento = DateTime.UtcNow + limite;
        while (DateTime.UtcNow < vencimiento)
        {
            if (await page.EvaluateAsync<string?>(GuionEstadoDelAnalisis) is not null)
                return true;

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>
    /// Lee el estado para el mensaje de error. Va en su propio try porque un
    /// diagnóstico no puede tapar el fallo que describe: si la página ya no
    /// responde, lo que hay que ver sigue siendo el timeout, no la excepción
    /// de haber intentado explicarlo.
    /// </summary>
    private static async Task<string> LeerEstadoDelAnalisisAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string?>(GuionEstadoDelAnalisis) ?? "inerte";
        }
        catch (PlaywrightException)
        {
            return "no se pudo leer (la página ya no responde)";
        }
    }

    private static async Task<string?> LeerAlertaDeFormularioAsync(IPage page)
    {
        var alerta = page.Locator(".alerta-formulario");
        return await alerta.CountAsync() > 0 ? (await alerta.First.InnerTextAsync()).Trim() : null;
    }

    /// <summary>
    /// El archivo por encima del tamaño máximo no pinta .alerta-formulario:
    /// avisa por toast y sale sin tocar _analizando
    /// (ManejarArchivoSeleccionadoAsync), así que sin esto ese caso sería
    /// indistinguible de un 'change' perdido.
    /// </summary>
    private static async Task<string> DescribirToastsAsync(IPage page)
    {
        try
        {
            var toasts = await page.Locator(".toast").AllInnerTextsAsync();
            return toasts.Count == 0 ? string.Empty : $" Toasts visibles: {string.Join(" | ", toasts).Trim()}.";
        }
        catch (PlaywrightException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Lee el número mostrado por una TarjetaMetrica de las pantallas de
    /// importación ("Clientes nuevos", "Documentos creados"…) — cada
    /// etiqueta es única dentro de la pantalla, así que HasText sobre
    /// ".tarjeta-metrica" no ambigua entre tarjetas.
    /// </summary>
    public static async Task<string> LeerMetricaAsync(IPage page, string etiqueta)
    {
        var tarjeta = page.Locator(".tarjeta-metrica", new PageLocatorOptions { HasText = etiqueta });
        return (await tarjeta.Locator(".tarjeta-metrica-valor").InnerTextAsync()).Trim();
    }

    /// <summary>
    /// Sondea App_Data/logs (sink de fichero de Serilog de la app real que
    /// arrancó WebAppFixture — ver el comentario largo de esa clase) hasta
    /// encontrar una línea que contenga <paramref name="textoBuscado"/>, o
    /// agota el presupuesto. Existe para probar por sensibilidad que un
    /// <c>catch</c> registra de verdad la excepción y no solo cambia
    /// <c>_mensajeError</c> en la UI — ver Importacion_archivo_ilegible_deja_rastro
    /// en ImportacionTests. Se abre con FileShare.ReadWrite porque Serilog
    /// mantiene el fichero abierto para escritura mientras la app real sigue
    /// corriendo.
    /// </summary>
    public static async Task<string> EsperarLineaEnLogDeLaAppAsync(string textoBuscado, TimeSpan presupuesto)
    {
        var limite = DateTime.UtcNow + presupuesto;
        Exception? ultimoErrorDeLectura = null;

        while (DateTime.UtcNow < limite)
        {
            try
            {
                var directorioLogs = DirectorioLogsCaeManagerWeb();
                if (Directory.Exists(directorioLogs))
                {
                    foreach (var ruta in Directory.GetFiles(directorioLogs, "log-*.txt")
                                 .OrderByDescending(File.GetLastWriteTimeUtc))
                    {
                        using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var lector = new StreamReader(flujo);
                        var contenido = await lector.ReadToEndAsync();
                        var linea = contenido
                            .Split('\n')
                            .LastOrDefault(l => l.Contains(textoBuscado, StringComparison.Ordinal));
                        if (linea is not null)
                            return linea.Trim();
                    }
                }
            }
            catch (IOException ex)
            {
                // El sink puede tener el fichero bloqueado un instante mientras rota o escribe — se reintenta.
                ultimoErrorDeLectura = ex;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"No apareció ninguna línea con \"{textoBuscado}\" en App_Data/logs tras {presupuesto.TotalSeconds:F0}s."
            + (ultimoErrorDeLectura is null ? string.Empty : $" Último error de lectura: {ultimoErrorDeLectura.Message}"));
    }

    /// <summary>
    /// Mismo criterio de resolución de ruta que
    /// WebAppFixture.LocalizarCaeManagerWebDll (duplicado en vez de
    /// referenciado: es privado allí y este helper vive del lado del test,
    /// no de la fixture) — sube desde el binario de este proyecto de test
    /// hasta la raíz del repo (marcada por CaeManager.slnx) y baja al
    /// content root real de CaeManager.Web.
    /// </summary>
    private static string DirectorioLogsCaeManagerWeb()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;

        if (directorio is null)
            throw new InvalidOperationException($"No se encontró la raíz del repo (CaeManager.slnx) subiendo desde {AppContext.BaseDirectory}.");

#if DEBUG
        const string configuracion = "Debug";
#else
        const string configuracion = "Release";
#endif

        return Path.Combine(directorio.FullName, "src", "CaeManager.Web", "bin", configuracion, "net10.0", "App_Data", "logs");
    }
}
