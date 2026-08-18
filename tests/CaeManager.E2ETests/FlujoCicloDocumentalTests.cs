using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre el último flujo de demo pendiente de Horizonte 1.6
/// (MACRO_PLAN_2026-08-13.md § 1.6, "el ciclo documental entero"): subida →
/// IA → validación → vencimiento → reclamación → renovación. Un único test
/// encadenado, mismo criterio que FlujoCriticoTests: cada paso depende del
/// resultado del anterior (la revisión IA solo existe si la subida activó
/// el análisis; la reclamación necesita el Documento con vencimiento ya
/// creado; la renovación edita ese mismo Documento) y dividirlo en varios
/// tests solo añadiría fragilidad de orden de ejecución sin ganar
/// aislamiento real.
///
/// El paso de IA usa <c>ProveedorFalsoDocumentAI</c> (ver
/// WebAppFixture.cs, variable "DocumentosIa__ProveedorFalsoActivo"), no un
/// proveedor real: Anthropic/Gemini/Mistral son "inertes" sin ApiKey en
/// este proceso de todos modos (ver AnthropicDocumentAIProvider) y, aunque
/// tuvieran clave, un test de flujo de aplicación no debe depender de un
/// modelo de pago, lento y no determinista solo para confirmar que la cola
/// de análisis, la revisión humana y el resto de la tubería funcionan. El
/// proveedor falso devuelve confianza fija en 50% a propósito — por debajo
/// del umbral de 70% de VerificacionIaDocumentoService — para que la
/// subida genere una RevisionIaDocumento pendiente de verdad, que es lo
/// que este test valida en /documentos/revision-ia.
/// </summary>
[Collection("AppCollection")]
public class FlujoCicloDocumentalTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Ciclo_documental_subida_ia_validacion_vencimiento_reclamacion_renovacion()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var nombreTipoDocumento = $"CicloDocumental Tipo {sufijo}";
        var razonSocialCliente = $"CicloDocumental Cliente {sufijo}";
        var razonSocialEmpresa = $"CicloDocumental Empresa {sufijo}";
        var nombreCentro = $"CicloDocumental Centro {sufijo}";
        var nombreTrabajador = "CicloDocumental";
        var apellidosTrabajador = $"E2E {sufijo}";
        var dniTrabajador = Ayudas.GenerarDniValido(88_000_901);
        var nombreContacto = $"Contacto CicloDocumental {sufijo}";
        var emailContacto = $"contacto.ciclodocumental.{sufijo}@e2e.test";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        var drawer = page.Locator(".drawer-panel");

        // --- Paso 0: Tipo de documento propio, con Verificación IA activada ---
        // Ningún tipo del catálogo semilla trae VerificacionIaActiva=true por
        // defecto (ver TipoDocumentoSeedData) — CrearDocumentoCommand no
        // encolaría ningún análisis si se reutilizara uno existente sin
        // tocarlo. Orden=0 lo deja siempre primero en /tipos-documento (el
        // catálogo semilla empieza en 1), sin tener que paginar para
        // encontrarlo — esa pantalla no tiene buscador de texto.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/tipos-documento");
        await page.GetByText("+ Nuevo tipo").ClickAsync();
        await drawer.GetByLabel("Nombre").FillAsync(nombreTipoDocumento);
        await drawer.GetByLabel("Orden").FillAsync("0");
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        var filaTipo = page.Locator("tr", new PageLocatorOptions { HasText = nombreTipoDocumento });
        await filaTipo.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await filaTipo
            .GetByTitle("Verifica por IA el tipo, fecha de emisión y firma del documento contra lo introducido al subirlo, con confidence score")
            .Locator("input")
            .CheckAsync();

        // --- Paso 1: Cliente → Empresa → Centro encadenados (alta guiada) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/alta-guiada");

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "1. Cliente" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await page.GetByLabel("CIF", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_501));
        await page.GetByText("Guardar y continuar a Empresa").ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "2. Empresa" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        await page.GetByLabel("CIF", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_502));
        await page.GetByText("Guardar y continuar a Centro").ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "3. Centro" }).WaitForAsync();
        await page.GetByLabel("Nombre", new PageGetByLabelOptions { Exact = true }).FillAsync(nombreCentro);
        await page.GetByText("Guardar centro").ClickAsync();
        // "Terminar aquí" solo se pinta con _centrosCreados == 0 (ver
        // AltaGuiada.razor) — que desaparezca es la señal de que el Centro
        // se guardó de verdad, mismo criterio que AltaGuiadaTests.
        await Expect(page.GetByText("Terminar aquí")).Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // --- Paso 2: Trabajador de esa Empresa ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByText("+ Nuevo trabajador").First.ClickAsync();

        // DDL-076 (resolución en silencio con una única Empresa) vs. selector
        // real cuando ya hay varias — "AppCollection" acumula Empresas de
        // otros tests, así que se comprueba visibilidad real en vez de
        // asumir cuál de las dos ramas pinta Blazor (mismo criterio que
        // FlujoBandejaPriorizadaTests).
        var comboEmpresa = drawer.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Empresa" });
        await page.WaitForTimeoutAsync(300);
        if (await comboEmpresa.IsVisibleAsync())
            await comboEmpresa.SelectOptionAsync(new SelectOptionValue { Label = razonSocialEmpresa });
        else
            await drawer.GetByText(razonSocialEmpresa).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        await drawer.GetByLabel("Documento de identidad (DNI, NIE, TIE o pasaporte)").FillAsync(dniTrabajador);
        await drawer.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombreTrabajador);
        await drawer.GetByLabel("Apellidos").FillAsync(apellidosTrabajador);
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Paso 3 (Subida): Documento del Trabajador, vencimiento a 10 días ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await page.GetByText("+ Nuevo documento").First.ClickAsync();

        await drawer.GetByLabel("Trabajador", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync($"{nombreTrabajador} {apellidosTrabajador} ({dniTrabajador})");
        await page.WaitForTimeoutAsync(500);
        await drawer.GetByLabel("Tipo de documento").SelectOptionAsync(new SelectOptionValue { Label = nombreTipoDocumento });

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        await drawer.GetByLabel("Fecha de emisión", new LocatorGetByLabelOptions { Exact = true }).FillAsync(hoy.ToString("yyyy-MM-dd"));
        await drawer.GetByLabel("Fecha de vencimiento").FillAsync(hoy.AddDays(10).ToString("yyyy-MM-dd"));

        var rutaPdf = Ayudas.GenerarPdfDePruebaEnDisco();
        try
        {
            await drawer.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaPdf);
            await drawer.GetByText("Archivo adjuntado correctamente.").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
            await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        }
        finally
        {
            File.Delete(rutaPdf);
        }

        // --- Paso 4 (Vencimiento, primera lectura): semáforo "Urgente" a 10 días ---
        // Mismo umbral que FlujoCriticoTests (rojo = 15 días) — se reutiliza
        // el patrón ya establecido en vez de reinventar el cálculo aquí.
        await page.GetByPlaceholder("Buscar por propietario o tipo de documento…").FillAsync(apellidosTrabajador);
        var filaDocumento = page.Locator("tr", new PageLocatorOptions { HasText = apellidosTrabajador });
        await filaDocumento.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        var insigniaEstado = filaDocumento.Locator(".badge-peligro");
        await insigniaEstado.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        Assert.Equal("Urgente", (await insigniaEstado.InnerTextAsync()).Trim());

        // --- Paso 5 (IA + Validación): la revisión IA aparece y se resuelve ---
        // /documentos/revision-ia carga sus datos una sola vez al entrar
        // (OnInitializedAsync, sin push del servidor) — el análisis lo
        // termina ProcesadorAnalisisDocumentoHostedService en un proceso
        // aparte, sondeando cada 5s (ver ese archivo), así que hay que
        // recargar la página en bucle hasta que aparezca, no basta con
        // esperar sobre el DOM ya cargado.
        var filaRevision = await EsperarFilaRevisionIaAsync(page, fixture.BaseUrl, apellidosTrabajador);

        // Confirma que la extracción determinista de ProveedorFalsoDocumentAI
        // llegó de verdad hasta la UI, a través de todo el router
        // (clasificación → OCR/estructuración → RevisionIaDocumento): 50%
        // de confianza y el motivo que ComputarMotivos genera para ese caso.
        await Expect(filaRevision).ToContainTextAsync("50%");
        await Expect(filaRevision).ToContainTextAsync("Confianza baja (50%)");

        // Segunda comprobación: TrabajoAnalisisDocumento.MarcarCompletado() y
        // la NotificacionUsuario se guardan en un SaveChangesAsync posterior
        // al que ya creó la RevisionIaDocumento (ver
        // ProcesadorAnalisisDocumentoHostedService.ProcesarPendientesDelTenantAsync)
        // — la fila de arriba puede encontrarse un instante antes de que la
        // notificación exista todavía.
        await DescartarNotificacionPendienteSiApareceAsync(page);
        await filaRevision.GetByText("Marcar como revisado").ClickAsync();
        await filaRevision.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Paso 6 (Reclamación): asignar el Trabajador a su Centro, añadir
        // un contacto de agenda y enviar la reclamación ---
        // EnviarReclamacionCommand exige una Asignación activa del
        // Trabajador a un Centro del Cliente (ver ObtenerLoteReclamacionQuery)
        // — nada en el alta guiada ni en la creación del Trabajador la crea
        // sola, hace falta el paso explícito de /trabajadores.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByPlaceholder("Buscar por nombre, apellidos, alias o DNI…").FillAsync(apellidosTrabajador);
        var filaTrabajador = page.Locator("tr", new PageLocatorOptions { HasText = apellidosTrabajador });
        await filaTrabajador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        await page.GetByText("Selección múltiple").ClickAsync();
        await filaTrabajador.Locator("input[type=\"checkbox\"]").CheckAsync();

        await page.Locator(".barra-acciones-lote").GetByText("Asignar a centro…").ClickAsync();
        // Sin Exact/scope, GetByLabel("Centro") también resuelve el propio
        // <div role="dialog"> del modal: su nombre accesible viene de
        // aria-labelledby → "Asignar 1 trabajador(es) a un centro", que
        // contiene "centro" como subcadena (visto en CI —
        // "strict mode violation... resolved to 2 elements").
        //
        // CampoBuscarSelect resuelve por texto EXACTO de la opción — y la
        // opción de Centro no es solo el nombre: OpcionesCentrosParaAsignar
        // (Trabajadores.razor.cs) la construye como "{Nombre} ({Cliente})"
        // para poder distinguir centros del mismo nombre en clientes
        // distintos. Rellenar solo con nombreCentro no coincidía con
        // ninguna opción — _centroIdParaAsignar se quedaba vacío y
        // "Asignar" fallaba en silencio con el toast "Selecciona un
        // centro.", sin cerrar el modal (visto en CI: timeout esperando a
        // que ".modal-pie" desapareciera).
        await page.Locator(".modal-cuerpo").GetByLabel("Centro", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync($"{nombreCentro} ({razonSocialCliente})");
        await page.WaitForTimeoutAsync(500);
        // El botón de confirmar cambia de texto a "Asignar igualmente" si
        // quedan documentos obligatorios sin cubrir (ver Trabajadores.razor.cs,
        // ObtenerDocumentosFaltantesParaAsignacionQuery) — este Trabajador de
        // prueba solo tiene el Documento de este test, así que casi seguro
        // los tiene: se acepta cualquiera de los dos textos.
        await page.Locator(".modal-pie").GetByText(new Regex("^Asignar")).ClickAsync();
        await page.Locator(".modal-pie").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos?pestana=reclamaciones");

        // La pestaña abre en "Enviadas" (el historial de lotes ya enviados,
        // que es lo que se consulta a diario) — componer/enviar, que es lo
        // que ejercita este paso, vive detrás de "+ Nueva reclamación" (ver
        // ReclamacionesTab: _vista = "historial" por defecto, y
        // CambiarVistaAsync carga los lotes la primera vez que se entra).
        // Por rol y no GetByText: el propio estado vacío del historial cita
        // el botón por su nombre ('Los lotes que envíes desde "+ Nueva
        // reclamación"…'), así que el texto suelto resuelve a 2 elementos.
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ Nueva reclamación" }).ClickAsync();

        var tarjetaCliente = page.Locator(".tarjeta-reclamacion", new PageLocatorOptions { HasText = razonSocialCliente });
        await tarjetaCliente.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // Sin contacto en la agenda todavía — ReclamacionesTab ofrece darlo de
        // alta sin salir de la pantalla (ModalContactoAgenda,
        // PredeterminadoPorDefecto=true), que es justo lo que hace falta para
        // que la agenda resuelva un destinatario.
        await tarjetaCliente.GetByText("Añadir contacto").First.ClickAsync();
        await page.Locator(".modal-cuerpo").GetByLabel("Nombre").FillAsync(nombreContacto);
        await page.Locator(".modal-cuerpo").GetByLabel("Email").FillAsync(emailContacto);
        await page.Locator(".modal-pie").GetByText("Guardar").ClickAsync();

        // Contacto predeterminado ⇒ ya sale marcado en _contactosPorCliente
        // al recargar (ver ReclamacionesTab.razor) — no hace falta marcar
        // ninguna casilla a mano.
        await tarjetaCliente.GetByText(emailContacto).WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // El botón "Enviar reclamación (N)" en sí (ver AccionesHeader de
        // ReclamacionesTab.razor) — Deshabilitado="@(seleccionados.Count == 0
        // || ContactosMarcados(lote.ClienteId).Count == 0)". El diagnóstico
        // de una ronda anterior en CI (InnerTextAsync de la tarjeta)
        // confirmó que el texto SÍ está en el DOM — el fallo real era de
        // actionability (visible/habilitado/estable), no de contenido.
        // Se comprueba Deshabilitado explícitamente antes del clic: si de
        // verdad está deshabilitado, esto falla al instante con un mensaje
        // claro en vez de esperar 30s a un timeout genérico de Playwright.
        var botonEnviarReclamacion = tarjetaCliente.GetByRole(
            AriaRole.Button, new LocatorGetByRoleOptions { NameRegex = new Regex("^Enviar reclamación") });
        await botonEnviarReclamacion.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        Assert.False(
            await botonEnviarReclamacion.IsDisabledAsync(),
            "El botón \"Enviar reclamación\" está deshabilitado — seleccionados o ContactosMarcados vacío en ReclamacionesTab (ver DrawerGestionDocumento/ModalContactoAgenda).");
        await botonEnviarReclamacion.ClickAsync();

        // "Nunca reclamado." pasa a "Última reclamación: hoy." solo si
        // EnviarReclamacionCommand terminó con éxito y persistió la
        // ReclamacionDocumental — confirmación funcional, no un toast.
        await tarjetaCliente.GetByText("Última reclamación: hoy.").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // --- Paso 7 (Renovación): nueva fecha de vencimiento, el semáforo pasa a "Vigente" ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await page.GetByPlaceholder("Buscar por propietario o tipo de documento…").FillAsync(apellidosTrabajador);
        await filaDocumento.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // La fila puede encontrarse ANTES de que el debounce de CampoTexto
        // (300 ms) dispare la recarga: entonces QuickGrid reconstruye el
        // <tr> con el menú ya abierto encima y el clic sobre "Renovar" muere
        // con "element is not stable" / "element was detached from the DOM".
        // El reintento de AbrirMenuAccionesAsync cubre el clic del
        // disparador, no el del item del panel — así que se espera a que la
        // recarga por debounce haya pasado antes de abrir nada.
        await page.WaitForTimeoutAsync(800);

        // MenuAcciones.razor abre el desplegable con un @onclick server-side
        // (alterna _abierto e ida y vuelta por SignalR, no instantáneo) —
        // visto en CI: el primer clic puede perderse si QuickGrid todavía
        // está reconstruyendo la fila justo en ese instante (la búsqueda de
        // más arriba acaba de refiltrar el grid), así que se reintenta en
        // vez de asumir que un único clic siempre llega.
        await AbrirMenuAccionesAsync(filaDocumento);
        await filaDocumento.Locator(".menu-acciones-panel").GetByText("Renovar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Fecha de emisión sin tocar (sigue siendo hoy — evita el diálogo de
        // "vigencia anterior" de GuardarAsync, que solo dispara si la nueva
        // fecha de emisión es ANTERIOR a la que ya tenía el documento). Solo
        // se extiende el vencimiento, muy por encima del umbral ámbar (30
        // días, ver ParametroSistemaSeedData) para que el badge cambie de
        // verdad a "Vigente" y no se quede en "Próximo".
        await drawer.GetByLabel("Fecha de vencimiento").FillAsync(hoy.AddDays(400).ToString("yyyy-MM-dd"));
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await page.GetByPlaceholder("Buscar por propietario o tipo de documento…").FillAsync(apellidosTrabajador);
        await filaDocumento.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        var insigniaVigente = filaDocumento.Locator(".badge-exito");
        await insigniaVigente.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        Assert.Equal("Vigente", (await insigniaVigente.InnerTextAsync()).Trim());
    }

    /// <summary>
    /// /documentos/revision-ia no se actualiza sola cuando termina un
    /// análisis en segundo plano (ver el comentario del Paso 5 más arriba) —
    /// se recarga en bucle hasta encontrar la fila o agotar el presupuesto
    /// de tiempo. 90s (antes 45s) cubre con margen el sondeo de 5s de
    /// ProcesadorAnalisisDocumentoHostedService más la elección de líder y
    /// el resto de la tubería (clasificación, extracción, guardado) — el
    /// margen original ya no bastaba con la suite de AppCollection tan
    /// crecida esta noche (varios flujos nuevos compitiendo por el mismo
    /// runner de CI), sin que cambiara nada en la tubería en sí.
    /// </summary>
    private static async Task<ILocator> EsperarFilaRevisionIaAsync(IPage page, string baseUrl, string apellidosTrabajador)
    {
        var limite = DateTime.UtcNow.AddSeconds(90);

        while (true)
        {
            await Ayudas.NavegarYEsperarAsync(page, $"{baseUrl}/documentos/revision-ia");
            await DescartarNotificacionPendienteSiApareceAsync(page);
            var fila = page.Locator("tr", new PageLocatorOptions { HasText = apellidosTrabajador });
            if (await fila.CountAsync() > 0)
                return fila;

            if (DateTime.UtcNow >= limite)
                throw new TimeoutException(
                    $"La revisión IA de \"{apellidosTrabajador}\" no apareció en /documentos/revision-ia tras 90s.");

            await page.WaitForTimeoutAsync(2_000);
        }
    }

    /// <summary>
    /// NotificacionesPopup (ver MainLayout.razor) es un modal bloqueante
    /// (CerrarAlHacerClicFuera=false) que aparece en CUALQUIER navegación
    /// completa mientras el Administrador tenga una <c>NotificacionUsuario</c>
    /// sin leer — y la verificación IA de este test genera una al completar
    /// (ver ProcesadorAnalisisDocumentoHostedService.AvisarSiCorrespondeAsync).
    /// Descubierto en CI (no una hipótesis): sin este descarte, el "Marcar
    /// como revisado" de más abajo fallaba con "modal-superposicion
    /// intercepts pointer events" — y peor, al quedar sin leer, la MISMA
    /// notificación volvía a bloquear el primer clic de cualquier otro test
    /// de "AppCollection" que iniciara sesión después con el mismo
    /// Administrador (la cuenta la comparte toda la suite). Se descarta con
    /// "Omitir" (solo marca MarcarNotificacionLeidaCommand, sin navegar) en
    /// cuanto aparece — la marca queda persistida, así que basta una vez
    /// para el resto de este test y para toda la suite.
    ///
    /// Espera explícitamente a que la superposición desaparezca del DOM
    /// tras el clic: ClickAsync solo confirma que el evento llegó al
    /// navegador, no que el viaje de ida y vuelta al servidor
    /// (MarcarNotificacionLeidaCommand) ya terminó y Blazor ya desmontó el
    /// modal — visto en CI, una llamada posterior a este mismo método podía
    /// resolver el MISMO botón a medio desmontar y fallar con "element was
    /// detached from the DOM" en vez de simplemente no encontrar nada.
    /// </summary>
    private static async Task DescartarNotificacionPendienteSiApareceAsync(IPage page)
    {
        var boton = page.GetByText("Omitir");
        if (await boton.CountAsync() == 0)
            return;

        await boton.ClickAsync();
        await page.Locator(".modal-superposicion").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
    }

    /// <summary>
    /// Abre el desplegable "⋯" de <c>MenuAcciones.razor</c> (alterna
    /// <c>_abierto</c> con un @onclick server-side, ida y vuelta por
    /// SignalR) — reintenta el clic hasta 3 veces si el panel no aparece a
    /// tiempo, visto en CI justo después de refiltrar un QuickGrid: el
    /// primer clic puede llegar mientras la fila todavía se está
    /// reconstruyendo y perderse sin que Playwright lo reporte como error
    /// (el botón sigue ahí, solo que ese clic en concreto no le llegó al
    /// circuito).
    /// </summary>
    private static async Task AbrirMenuAccionesAsync(ILocator fila)
    {
        var boton = fila.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Más acciones" });
        var panel = fila.Locator(".menu-acciones-panel");

        for (var intento = 1; intento <= 3; intento++)
        {
            await boton.ClickAsync();
            try
            {
                await panel.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
                return;
            }
            catch (TimeoutException) when (intento < 3)
            {
                // Reintento — ver el comentario del método.
            }
        }
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
