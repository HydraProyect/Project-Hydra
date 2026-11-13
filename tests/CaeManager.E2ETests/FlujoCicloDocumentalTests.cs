using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre el último flujo de demo pendiente de Horizonte 1.6
/// (Project-Hydra-Negocio/MACRO_PLAN_2026-08-13.md § 1.6, "el ciclo documental entero"): subida →
/// IA → validación → vencimiento → reclamación → renovación. Un único test
/// encadenado, mismo criterio que FlujoCriticoTests: cada paso depende del
/// resultado del anterior (la revisión IA solo existe si la subida activó
/// el análisis; la reclamación necesita el Documento con vencimiento ya
/// creado; la renovación edita ese mismo Documento) y dividirlo en varios
/// tests solo añadiría fragilidad de orden de ejecución sin ganar
/// aislamiento real.
///
/// La reclamación, sin SMTP ni buzón conectado en este proceso, termina en el
/// fallo visible que recibe el Gestor CAE y no se registra (ver el paso 6).
///
/// El paso de IA usa <c>ProveedorFalsoDocumentAI</c> (ver
/// WebAppFixture.cs, variable "DocumentosIa__ProveedorFalsoActivo"), no un
/// proveedor real: Anthropic es "inertes" sin ApiKey en
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
        await using var traza = TrazaAlFallar.Armar(contexto);
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        var drawer = page.Locator(".drawer-panel");

        // --- Paso 0: Tipo de documento propio, con Verificación IA activada ---
        // Ningún tipo del catálogo semilla trae VerificacionIaActiva=true por
        // defecto (ver TipoDocumentoSeedData) — CrearDocumentoCommand no
        // encolaría ningún análisis si se reutilizara uno existente sin
        // tocarlo. Orden=0 lo deja siempre primero en /tipos-documento (el
        // catálogo semilla empieza en 1), sin tener que paginar ni buscar
        // para encontrarlo por muchos tipos que hayan creado otros tests de
        // "AppCollection".
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/tipos-documento");
        await page.GetByRole(AriaRole.Button, new() { Name = "+ Nuevo tipo", Exact = true }).ClickAsync();
        await drawer.GetByLabel("Nombre").FillAsync(nombreTipoDocumento);
        await drawer.GetByLabel("Orden").FillAsync("0");
        await drawer.Locator(".drawer-pie").GetByRole(AriaRole.Button, new() { Name = "Guardar", Exact = true }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        var filaTipo = page.GetByRole(AriaRole.Row, new() { Name = nombreTipoDocumento });
        await filaTipo.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        var interruptorVerificacionIa = filaTipo.GetByTitle(
            "Verifica por IA el tipo, fecha de emisión y firma del documento contra lo introducido al subirlo, con confidence score");
        await interruptorVerificacionIa.Locator("input").CheckAsync();

        // CheckAsync solo confirma que el navegador marcó la casilla: el
        // @onchange de Blazor es una ida y vuelta por SignalR, y
        // VerificacionIaActiva no queda persistido hasta que
        // ActualizarVerificacionIaGlobalCommand termina y CargarAsync repinta
        // la etiqueta releyendo de la base ("Desactivada" → "Activa"). El
        // GotoAsync del Paso 1 tira el circuito entero, y con él el ámbito de
        // DI: un comando todavía en vuelo muere a mitad de consulta con
        // ObjectDisposedException sobre la NpgsqlConnection ya desechada.
        //
        // No es teórico — es la causa medida del fallo de este test (log de la
        // app, cinco ejecuciones): el comando tarda ~60 ms y el
        // POST /_blazor/disconnect llegaba a los ~50 ms. Cuando perdía esa
        // carrera, el tipo se quedaba con VerificacionIaActiva=false,
        // CrearDocumentoCommand no encolaba ningún TrabajoAnalisisDocumento y
        // el botón de la cola de /documentos/revision-ia NO LLEGABA A EXISTIR — no llegaba
        // tarde. Por eso subir el presupuesto de espera no arreglaba nada (con
        // 300 s tampoco aparecía) y por eso el fallo parecía mudo: el comando
        // revienta en un circuito ya cerrado, así que no llega a ninguna capa
        // que el test pueda ver — la traza está en el log de archivo de la
        // app (App_Data/logs de CaeManager.Web, ver WebAppFixture).
        //
        // Texto exacto, no subcadena: "Desactivada" contiene "activa".
        await Expect(interruptorVerificacionIa).ToHaveTextAsync(
            "Activa", new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });

        // El tipo creado aquí no pasa sensibilidad: cuenta como de salud y su lectura nace apagada (DPA 16.4).
        // La verificación exige lectura, así que el Administrador la enciende también, con la misma espera.
        var interruptorLecturaIa = filaTipo.GetByTitle("Lectura automática por IA para este tipo de documento, en todos los Clientes");
        await interruptorLecturaIa.Locator("input").CheckAsync();
        await Expect(interruptorLecturaIa).ToHaveTextAsync(
            "Activa", new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });

        // --- Paso 1: Empresa → Cliente → Centro encadenados (alta guiada) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/alta-guiada");

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "1. Empresa" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        // Paso 1: "Identificación fiscal *": obligatoria, igual que en el paso 2.
        await page.GetByLabel("Identificación fiscal *", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_502));
        await page.GetByRole(AriaRole.Button, new() { Name = "Guardar y continuar", Exact = true }).ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "2. Cliente" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await page.GetByLabel("Identificación fiscal *", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_995_501));
        await page.GetByRole(AriaRole.Button, new() { Name = "Guardar y continuar a Centro", Exact = true }).ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "3. Centro" }).WaitForAsync();
        await page.GetByLabel("Nombre", new PageGetByLabelOptions { Exact = true }).FillAsync(nombreCentro);
        await page.GetByRole(AriaRole.Button, new() { Name = "Guardar centro", Exact = true }).ClickAsync();
        // "Terminar aquí" solo se pinta con _centrosCreados == 0 (ver
        // AltaGuiada.razor) — que desaparezca es la señal de que el Centro
        // se guardó de verdad, mismo criterio que AltaGuiadaTests.
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Terminar aquí", Exact = true })).Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // --- Paso 2: Trabajador de esa Empresa ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.Locator("[data-keytip-contenedor=\"N\"]").GetByRole(AriaRole.Button, new() { Name = "+ Nuevo trabajador", Exact = true }).ClickAsync();

        // DDL-076 (resolución en silencio con una única Empresa) vs. selector
        // real cuando ya hay varias — "AppCollection" acumula Empresas de
        // otros tests, así que se comprueba visibilidad real en vez de
        // asumir cuál de las dos ramas pinta Blazor (mismo criterio que
        // FlujoBandejaPriorizadaTests).
        await Ayudas.SeleccionarEmpresaEnDrawerTrabajadorAsync(drawer, razonSocialEmpresa);

        await drawer.GetByLabel("Documento de identidad (DNI, NIE, TIE o pasaporte)").FillAsync(dniTrabajador);
        await drawer.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombreTrabajador);
        await drawer.GetByLabel("Apellidos").FillAsync(apellidosTrabajador);
        // El alta de Trabajador es un panel de pasos: en el primero el primario es «Siguiente» y «Guardar y terminar» guarda ya.
        await drawer.Locator(".drawer-pie").GetByRole(AriaRole.Button, new() { Name = "Guardar y terminar", Exact = true }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Paso 3 (Subida): Documento del Trabajador, vencimiento a 10 días ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await page.Locator("[data-keytip-contenedor=\"N\"]").GetByRole(AriaRole.Button, new() { Name = "+ Nuevo documento", Exact = true }).ClickAsync();

        await Ayudas.ElegirOpcionEnSelectorAvanzadoPorTextoAsync(page,
            drawer.GetByLabel("Trabajador", new LocatorGetByLabelOptions { Exact = true }),
            $"{nombreTrabajador} {apellidosTrabajador} ({razonSocialEmpresa})");
        await Ayudas.ElegirOpcionEnSelectorAvanzadoPorTextoAsync(page, drawer.GetByLabel("Tipo de documento"), nombreTipoDocumento);

        var hoy = Ayudas.HoyDeNegocio();
        await Ayudas.RellenarFechaAsync(drawer.GetByLabel("Fecha de emisión", new LocatorGetByLabelOptions { Exact = true }), hoy);
        await Ayudas.RellenarFechaAsync(drawer.GetByLabel("Fecha de vencimiento"), hoy.AddDays(10));

        var rutaPdf = Ayudas.GenerarPdfDePruebaEnDisco();
        try
        {
            await drawer.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaPdf);
            await drawer.GetByText("Archivo adjuntado correctamente.").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await drawer.Locator(".drawer-pie").GetByRole(AriaRole.Button, new() { Name = "Guardar", Exact = true }).ClickAsync();
            await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        }
        finally
        {
            File.Delete(rutaPdf);
        }

        // --- Paso 4 (Vencimiento, primera lectura): semáforo "Urgente" a 10 días ---
        // Mismo umbral que FlujoCriticoTests (rojo = 15 días) — se reutiliza
        // el patrón ya establecido en vez de reinventar el cálculo aquí.
        await page.GetByPlaceholder("Filtrar esta pantalla: Documento o propietario").FillAsync(apellidosTrabajador);
        var filaDocumento = page.GetByRole(AriaRole.Row, new() { Name = apellidosTrabajador });
        await filaDocumento.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        // Desde el 2026-10-08 Urgente y Próximo comparten la pastilla «Por vencer» (ámbar); lo que
        // dice que está a 10 días —dentro del umbral rojo— es el relativo de «Vence» (Estado ya no lo repite).
        var insigniaEstado = filaDocumento.Locator(".badge-advertencia");
        await insigniaEstado.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        Assert.Equal("Por vencer", (await insigniaEstado.InnerTextAsync()).Trim());
        Assert.Matches("^en (9|10|11) días$", (await filaDocumento.Locator(".documentos-vigencia-subtitulo").InnerTextAsync()).Trim());

        // --- Paso 5 (IA + Validación): la revisión IA aparece y se resuelve ---
        // /documentos/revision-ia carga sus datos una sola vez al entrar
        // (OnInitializedAsync, sin push del servidor) — el análisis lo
        // termina ProcesadorAnalisisDocumentoHostedService en un proceso
        // aparte, sondeando cada 5s (ver ese archivo), así que hay que
        // recargar la página en bucle hasta que aparezca, no basta con
        // esperar sobre el DOM ya cargado.
        var opcionRevision = await EsperarOpcionRevisionIaAsync(page, fixture.BaseUrl, apellidosTrabajador);

        // Confirma que la extracción determinista de ProveedorFalsoDocumentAI
        // llegó de verdad hasta la UI, a través de todo el router
        // (clasificación → OCR/estructuración → RevisionIaDocumento): 50%
        // de confianza en el desplegable de pendientes y el motivo que
        // ComputarMotivos genera para ese caso en el lateral de la revisión
        // seleccionada (D-49: documento a la izquierda, revisión en el lateral).
        await Expect(opcionRevision).ToContainTextAsync("Confianza 50 %");
        await opcionRevision.ClickAsync();

        var lateral = page.Locator(".revision-ia-lateral");
        var selector = lateral.Locator(".revision-ia-selector-boton");
        await Expect(selector).ToContainTextAsync(apellidosTrabajador, new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });
        await Expect(lateral.Locator(".revision-ia-motivo"))
            .ToContainTextAsync("Confianza baja (50%)");

        await lateral.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Descartar la lectura" }).ClickAsync();

        // El detalle y DialogoConfirmacion comparten el texto del botón de
        // descarte. Modal expone role="dialog" y toma su nombre accesible del
        // h2 con el título, por lo que este ámbito pulsa la confirmación y no
        // vuelve a pulsar el botón que abrió el diálogo.
        var dialogoDescartar = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Descartar la lectura de la IA" });
        await dialogoDescartar.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await dialogoDescartar.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Descartar la lectura" }).ClickAsync();
        // Resuelta la revisión, el avance automático pasa a la siguiente
        // pendiente o, si era la última, el lateral desaparece y queda el
        // estado sin pendientes. Se espera a cualquiera de los dos: el selector
        // ya no existe o ya no nombra a este Trabajador. Se lee su texto en vez
        // de filtrar el localizador por texto (ratchet E2E-locators).
        await EsperarQueElSelectorNoNombreAsync(page, apellidosTrabajador);

        // Que cambie la selección no prueba que la revisión haya salido de
        // pendientes: se recarga en frío (los datos vuelven del servidor) y se
        // comprueba que su opción ya no está en el desplegable. Si no queda
        // ninguna pendiente, no hay lateral ni desplegable que abrir.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos/revision-ia");
        // Solo se pinta una tarjeta: la del estado sin pendientes o la de la revisión.
        await page.Locator(".revision-ia-tarjeta").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        if (await page.Locator(".revision-ia-lateral").CountAsync() > 0)
        {
            Assert.True(await AbrirDesplegableDePendientesAsync(page), "El desplegable de pendientes no se abrió tras recargar /documentos/revision-ia.");
            var pendientes = await NombresDePendientesConElPanelAbiertoAsync(page);
            Assert.DoesNotContain(pendientes, nombre => nombre.Contains(apellidosTrabajador, StringComparison.Ordinal));
        }

        // --- Paso 6 (Reclamación): asignar el Trabajador a su Centro, añadir
        // un contacto de agenda y enviar la reclamación ---
        // EnviarReclamacionCommand exige una Asignación activa del
        // Trabajador a un Centro del Cliente (ver ObtenerLoteReclamacionQuery)
        // — nada en el alta guiada ni en la creación del Trabajador la crea
        // sola, hace falta el paso explícito de /trabajadores.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByPlaceholder("Filtrar esta pantalla: nombre, DNI o alias").FillAsync(apellidosTrabajador);
        var filaTrabajador = page.Locator(".marco-lista-trabajadores .tarjeta-fila-acordeon", new PageLocatorOptions { HasText = apellidosTrabajador });
        await filaTrabajador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        // La fila ya estaba en la lista sin filtrar, así que verla no dice que el filtro haya llegado, y la recarga del
        // filtro limpia la selección y rehace la barra de lote. La barrera es la lista filtrada: queda solo esa fila.
        await Expect(page.Locator(".marco-lista-trabajadores .fila-pulsable")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });

        // El ☑ de la cabecera de la lista (rediseño de listados, fase 1): un icono con nombre accesible, sin texto visible.
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Selección múltiple", Exact = true }).ClickAsync();
        await filaTrabajador.Locator("input[type=\"checkbox\"]").CheckAsync();

        await page.Locator(".barra-acciones-lote").GetByRole(AriaRole.Button, new() { Name = "Asignar a un centro", Exact = true }).ClickAsync();
        // Sin Exact/scope, GetByLabel("Centro") también resuelve el propio
        // <div role="dialog"> del modal: su nombre accesible viene de
        // aria-labelledby → "Asignar 1 trabajador a un centro", que
        // contiene "centro" como subcadena (visto en CI —
        // "strict mode violation... resolved to 2 elements").
        //
        // La opción de Centro no es solo el nombre: OpcionesCentrosParaAsignar
        // (Trabajadores.razor.cs) la construye como "{Nombre} ({Cliente})"
        // para poder distinguir centros del mismo nombre en clientes
        // distintos. Se busca y se pulsa esa opción en el combobox
        // (CampoBuscarSelect); sin elegirla, "Asignar" falla con el toast
        // "Selecciona un centro." y el modal no se cierra.
        await Ayudas.ElegirOpcionEnSelectorAvanzadoPorTextoAsync(page,
            page.Locator(".modal-cuerpo").GetByLabel("Centro", new LocatorGetByLabelOptions { Exact = true }),
            $"{nombreCentro} ({razonSocialCliente})");
        // El botón de confirmar cambia de texto a "Asignar igualmente" si al
        // comprobarlo faltaba algún documento de los que se piden (ver
        // Trabajadores.razor.cs, ObtenerDocumentosFaltantesParaAsignacionQuery)
        // — este Trabajador de
        // prueba solo tiene el Documento de este test, así que casi seguro
        // los tiene: se acepta cualquiera de los dos textos.
        await page.Locator(".modal-pie").GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Asignar") }).ClickAsync();
        await page.Locator(".modal-pie").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos?pestana=reclamaciones");
        // La pestaña abre por defecto en "Enviadas" (historial) — "+ Nueva
        // reclamación" (subvista, una pestaña desde D-30) revela la vista de
        // componer/enviar donde vive .tarjeta-reclamacion (ver
        // ReclamacionesTab.razor). GetByRole, no GetByText: el propio texto
        // del historial vacío menciona "+ Nueva reclamación" entre comillas,
        // así que GetByText a secas es ambiguo entre la pestaña y ese párrafo.
        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = "+ Nueva reclamación" }).ClickAsync();
        var tarjetaCliente = page.Locator(".tarjeta-reclamacion", new PageLocatorOptions { HasText = razonSocialEmpresa });
        await tarjetaCliente.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // Sin contacto en la agenda todavía — ReclamacionesTab ofrece darlo de
        // alta sin salir de la pantalla (ModalContactoAgenda,
        // PredeterminadoPorDefecto=true), que es justo lo que hace falta para
        // que la agenda resuelva un destinatario.
        await tarjetaCliente.GetByRole(AriaRole.Button, new() { Name = "Añadir contacto", Exact = true }).ClickAsync();
        await page.Locator("[role=dialog] .drawer-cuerpo").GetByLabel("Nombre").FillAsync(nombreContacto);
        await page.Locator("[role=dialog] .drawer-cuerpo").GetByLabel("Email").FillAsync(emailContacto);
        await page.Locator("[role=dialog] .drawer-pie").GetByRole(AriaRole.Button, new() { Name = "Guardar y terminar", Exact = true }).ClickAsync();

        // Contacto predeterminado ⇒ ya sale marcado en _contactosPorCliente
        // al recargar (ver ReclamacionesTab.razor) — no hace falta marcar
        // ninguna casilla a mano.
        await tarjetaCliente.GetByRole(AriaRole.Checkbox, new() { Name = emailContacto }).WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

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

        // Revisión previa al envío (D-01): el primer clic solo abre el modal.
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Confirmar y enviar", Exact = true })
            .ClickAsync();

        // El entorno E2E no tiene SMTP configurado ni buzón Microsoft 365
        // conectado, así que el correo de la reclamación no puede salir. Desde
        // la corrección del «error tragado» de RegistroEnvioReclamacionService,
        // eso es un fallo visible para el Gestor CAE y no se registra nada: antes
        // este paso esperaba «Última reclamación: hoy.» y solo pasaba porque el
        // fallo de SMTP se ignoraba. El camino con entrega real lo prueban
        // FalloSmtpDeLaReclamacionTests (Application) y ReclamacionesTabTests
        // (bUnit); un E2E con entrega real necesitaría un SMTP de pruebas.
        await Assertions.Expect(page.Locator(".toast.toast-error")
                .Filter(new LocatorFilterOptions { HasText = "No pudimos enviar el correo de la reclamación" }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Assertions.Expect(tarjetaCliente.GetByText("Nunca reclamado.")).ToBeVisibleAsync();
        await Assertions.Expect(tarjetaCliente.GetByText("Última reclamación: hoy.")).ToHaveCountAsync(0);

        // --- Paso 7 (Renovación): nueva fecha de vencimiento, el semáforo pasa a "Vigente" ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/documentos");
        await page.GetByPlaceholder("Filtrar esta pantalla: Documento o propietario").FillAsync(apellidosTrabajador);
        await filaDocumento.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // La fila ya no lleva menú «⋯»: «Renovar» es su acción rápida, que abre el mismo drawer.
        await filaDocumento.Locator("button.documentos-accion-rapida").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Renovar abre la fecha de emisión vacía: se escribe hoy (igual a la
        // que ya tenía el documento, lo que evita el diálogo de "vigencia
        // anterior" de GuardarAsync, que solo dispara si la nueva fecha de
        // emisión es ANTERIOR). Se extiende el vencimiento, muy por encima del
        // umbral ámbar (30 días, ver ParametroSistemaSeedData) para que el
        // badge cambie de verdad a "Vigente" y no se quede en "Próximo".
        await Ayudas.RellenarFechaAsync(drawer.GetByLabel("Fecha de emisión", new LocatorGetByLabelOptions { Exact = true }), hoy);
        await Ayudas.RellenarFechaAsync(drawer.GetByLabel("Fecha de vencimiento"), hoy.AddDays(400));
        await drawer.Locator(".drawer-pie").GetByRole(AriaRole.Button, new() { Name = "Guardar", Exact = true }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await page.GetByPlaceholder("Filtrar esta pantalla: Documento o propietario").FillAsync(apellidosTrabajador);
        await filaDocumento.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        // Lo correcto ya no lleva pastilla de color (2026-10-08): punto verde y texto gris.
        var insigniaVigente = filaDocumento.Locator("[data-pieza=estado-correcto]");
        await insigniaVigente.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        Assert.Equal("Vigente", (await insigniaVigente.InnerTextAsync()).Trim());
        traza.Superada();
    }

    /// <summary>
    /// /documentos/revision-ia no se actualiza sola cuando termina un
    /// análisis en segundo plano (ver el comentario del Paso 5 más arriba) —
    /// se recarga en bucle hasta encontrar la opción del desplegable o agotar el presupuesto
    /// de tiempo. 90s cubre con margen el sondeo de 5s de
    /// ProcesadorAnalisisDocumentoHostedService más la elección de líder y
    /// el resto de la tubería (clasificación, extracción, guardado): medido
    /// en el log de la app, la tubería entera tarda ~11s desde que el
    /// Documento se guarda hasta que la revisión es visible.
    ///
    /// El presupuesto ya se subió una vez de 45s a 90s culpando a la carga
    /// de la suite; esa lectura era falsa y queda corregida aquí. Cuando este
    /// test fallaba, la revisión no llegaba tarde: no llegaba a existir (el
    /// interruptor de Verificación IA del Paso 0 no se había persistido, ver
    /// allí), y con 300s tampoco aparecía. Si vuelve a agotarse el
    /// presupuesto, el sospechoso NO es el presupuesto — es que no se encoló
    /// ningún TrabajoAnalisisDocumento, y la traza está en el log de la app
    /// (App_Data/logs/log-*.txt del directorio de CaeManager.Web, sink de
    /// archivo de Serilog), no en la salida del test.
    ///
    /// Las pendientes viven en el desplegable del lateral (role="listbox"),
    /// que solo se pinta abierto: tras cada carga se abre con
    /// <see cref="AbrirDesplegableDePendientesAsync"/>.
    /// </summary>
    private static async Task<ILocator> EsperarOpcionRevisionIaAsync(IPage page, string baseUrl, string apellidosTrabajador)
    {
        var presupuesto = TimeSpan.FromSeconds(90);
        var limite = DateTime.UtcNow + presupuesto;

        while (true)
        {
            await Ayudas.NavegarYEsperarAsync(page, $"{baseUrl}/documentos/revision-ia");
            await AbrirDesplegableDePendientesAsync(page);

            var opcion = page.Locator(".revision-ia-opciones").GetByRole(
                AriaRole.Option,
                new LocatorGetByRoleOptions { Name = apellidosTrabajador });
            try
            {
                await opcion.WaitForAsync(new LocatorWaitForOptions { Timeout = 2_000 });
                return opcion;
            }
            catch (TimeoutException) { }

            if (DateTime.UtcNow >= limite)
                throw new TimeoutException(
                    $"La revisión IA de \"{apellidosTrabajador}\" no apareció en /documentos/revision-ia "
                    + $"tras {presupuesto.TotalSeconds:F0}s.");
        }
    }

    /// <summary>
    /// Abre el desplegable de pendientes del lateral. La página no se
    /// prerenderiza: el selector solo existe cuando el circuito ya pinta y los
    /// datos han cargado. Aun así, un clic puede tardar en volver por SignalR
    /// bajo la carga de la suite. Como pulsar con el panel abierto lo cierra,
    /// tras cada clic se espera con margen y solo se vuelve a pulsar si el
    /// servidor sigue diciendo que está cerrado (aria-expanded="false"). Si el
    /// panel se cerrara por un clic tardío, quien mire las opciones lo hace con
    /// <see cref="NombresDePendientesConElPanelAbiertoAsync"/>, que no da por
    /// buena una lectura con el panel cerrado. Devuelve si quedó abierto (no lo
    /// queda cuando no hay pendientes y el lateral no se pinta).
    /// </summary>
    private static async Task<bool> AbrirDesplegableDePendientesAsync(IPage page)
    {
        var panel = page.Locator(".revision-ia-pop");
        var disparador = page.Locator(".revision-ia-selector-boton");
        for (var intento = 0; intento < 3 && !await panel.IsVisibleAsync(); intento++)
        {
            try
            {
                if (await disparador.GetAttributeAsync("aria-expanded", new LocatorGetAttributeOptions { Timeout = 5_000 }) == "true")
                {
                    await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
                    break;
                }

                await disparador.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // Sin pendientes no hay selector, o el clic no volvió a tiempo: se comprueba aria-expanded antes de repetir.
            }
        }

        return await panel.IsVisibleAsync();
    }

    /// <summary>
    /// Nombres accesibles de las opciones del desplegable, leídos en una sola
    /// evaluación del DOM junto con la visibilidad del panel. Exige el panel
    /// abierto y al menos una opción en esa misma lectura: así «no aparece la
    /// revisión» nunca se concluye de un panel cerrado o vacío.
    /// </summary>
    private static async Task<string[]> NombresDePendientesConElPanelAbiertoAsync(IPage page)
    {
        var lectura = await page.EvaluateAsync<string[]?>(
            """
            () => {
                const panel = document.querySelector('.revision-ia-pop');
                if (!panel || panel.getClientRects().length === 0) return null;
                return [...panel.querySelectorAll('.revision-ia-opciones [role=option]')].map(o => o.textContent ?? '');
            }
            """);
        Assert.True(lectura is { Length: > 0 }, "El desplegable de pendientes no estaba abierto o no tenía opciones al leerlo.");
        return lectura!;
    }

    /// <summary>
    /// Espera hasta 15 s a que el selector del lateral desaparezca (no quedan
    /// pendientes) o deje de nombrar al Trabajador (el avance pasó a otra).
    /// Recuento y texto se leen en la misma evaluación del DOM, y Playwright la
    /// repite hasta que se cumple, sin esperas fijas.
    /// </summary>
    private static Task EsperarQueElSelectorNoNombreAsync(IPage page, string apellidosTrabajador) =>
        page.WaitForFunctionAsync(
            """
            nombre => {
                const selector = document.querySelector('.revision-ia-lateral .revision-ia-selector-boton');
                return !selector || !(selector.textContent ?? '').includes(nombre);
            }
            """,
            apellidosTrabajador,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
