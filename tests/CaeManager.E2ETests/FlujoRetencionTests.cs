using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// P1-19 de Project-Hydra-Negocio/MATURITY_REVIEW.md: retención de datos (Fase 60)
/// no tenía ningún E2E — solo CalculadoraRetencionDocumentoTests (Domain).
/// Cubre el ciclo completo de /retencion descrito en CLAUDE.md: "detectar →
/// avisar → autorizar con fecha → ejecutar", más la vía alternativa de
/// "descartar" — contra la app real, con RetencionDatos:Activa forzado a
/// true (WebAppFixtureConRetencionActiva; apagado en cualquier otro sitio,
/// ver CLAUDE.md).
///
/// Los datos purgables no se crean en el test: DatosPruebaSeeder siembra 6
/// "veteranos" (Pedro Picapiedra y compañía) específicamente para este
/// flujo — dados de baja hace ~6 años y con documentos vencidos hace ~6
/// años, bien pasado el plazo de retención por defecto (5 años). El
/// resultado de "Buscar" son siempre exactamente dos propuestas,
/// "Documentos" (18 registros = 3 documentos × 6 veteranos, incondicional)
/// y "Trabajadores dados de baja" — este segundo número SÍ varía entre
/// ejecuciones deterministas con semilla distinta: la Asignacion de cada
/// veterano solo se crea si la Empresa aleatoria que le tocó tiene al menos
/// un Centro (DatosPruebaSeeder.cs, bloque "Veteranos para la purga"), así
/// que el test no asume una cifra fija ahí — solo que la fila existe.
/// </summary>
[Collection("AppCollectionRetencion")]
public class FlujoRetencionTests(WebAppFixtureConRetencionActiva fixture)
{
    [Fact]
    public async Task Detectar_avisar_autorizar_y_ejecutar_una_purga_y_descartar_la_otra()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        await using var traza = TrazaAlFallar.Armar(contexto);
        var page = await contexto.NewPageAsync();

        // El ciclo lo recorre un Administrador propio del Tenant, sin cambiar
        // de Tenant activo: los "veteranos"
        // viven en el Cliente Delegante de demo, y DatosPruebaSeeder siembra
        // ahí mismo un juego completo de usuarios por rol.
        //
        // El Administrador del Operador CAE externo también administra la retención de este Tenant
        // beneficiario (regla del 2026-10-20, segundo test de esta clase), pero el ciclo completo se
        // recorre con el Administrador propio para no depender de esa vía.
        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailPrueba("administrador", 1), Ayudas.ContrasenaUsuariosPrueba);

        // Los usuarios prueba.<rol> arrancan con una notificación sin leer que
        // bloquea toda interacción hasta descartarla (ver Ayudas).

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/retencion");

        // --- Detectar ---
        var botonBuscar = page.GetByRole(AriaRole.Button, new() { Name = "Buscar datos que hayan cumplido plazo", Exact = true });
        await botonBuscar.ClickAsync();

        // Barrera (#2187): que haya una tabla visible no prueba que Buscar haya terminado. En el run 43122999105, anterior
        // a #2198, «He avisado a la organización» se pulsó a ~15 ms del fin de BuscarDatosPurgablesCommand y
        // MarcarTenantAvisadoCommand no llegó al mediador. Por qué se perdió ese clic no está demostrado. El mecanismo
        // probable era la carga inicial, que comparte la PuertaAccesoDatos del circuito con el Command: al correr
        // detrás de él, pintaba sus propuestas con la recarga de Buscar aún en vuelo. #2198 lo cierra en producto, y
        // esta espera no depende de esa causa: el test no toca la tabla mientras Buscar siga en curso. El aviso de
        // Buscar sale cuando el Command ha terminado, y el botón solo vuelve a habilitarse cuando BuscarAsync ha
        // terminado del todo, recarga incluida (Cargando="_buscando", Deshabilitado="… || _cargando"). Vale cualquiera
        // de los dos avisos de éxito: la barrera no depende de quién creó las propuestas, que se comprueban abajo.
        await Assertions.Expect(page.Locator(".anfitrion-toasts")).ToContainTextAsync(
            new Regex(@"propuesta\(s\) nueva\(s\) pendientes de revisión\.|Ningún dato ha cumplido todavía su plazo\."),
            new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });
        await Assertions.Expect(botonBuscar).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 15_000 });

        var tabla = page.Locator(".tabla-datos");
        await tabla.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        var filaDocumentos = tabla.Locator("tr", new LocatorLocatorOptions { HasText = "Documentos" });
        var filaTrabajadores = tabla.Locator("tr", new LocatorLocatorOptions { HasText = "Trabajadores dados de baja" });
        await filaDocumentos.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await filaTrabajadores.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        Assert.Contains("18", await filaDocumentos.InnerTextAsync());
        Assert.Contains("Pendiente de revisar", await filaDocumentos.InnerTextAsync());
        Assert.Contains("Pendiente de revisar", await filaTrabajadores.InnerTextAsync());

        // --- Avisar (solo la fila de Documentos sigue el camino completo) ---
        await filaDocumentos.GetByRole(AriaRole.Button, new() { Name = "He avisado a la organización", Exact = true }).ClickAsync();
        await AsegurarTextoEnFilaAsync(page, filaDocumentos, "Organización avisada");

        // --- Autorizar, con fecha de ejecución de hoy mismo para poder
        // ejecutar en el mismo test sin esperar 30 días ---
        await filaDocumentos.GetByRole(AriaRole.Button, new() { Name = "Autorizar", Exact = true }).ClickAsync();

        var modalAutorizar = page.GetByRole(AriaRole.Dialog, new() { Name = "Autorizar la destrucción", Exact = true });
        await modalAutorizar.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await Ayudas.RellenarFechaAsync(modalAutorizar.GetByLabel("Fecha de ejecución"), Ayudas.HoyDeNegocio());
        await modalAutorizar.Locator(".modal-pie").GetByRole(AriaRole.Button, new() { Name = "Autorizar", Exact = true }).ClickAsync();
        await EsperarCierreAsync(page, modalAutorizar);

        await AsegurarTextoEnFilaAsync(page, filaDocumentos, "Lista para ejecutar");

        // --- Ejecutar ---
        await filaDocumentos.GetByRole(AriaRole.Button, new() { Name = "Ejecutar ahora", Exact = true }).ClickAsync();

        var modalEjecutar = page.GetByRole(AriaRole.Dialog, new() { Name = "Ejecutar la destrucción", Exact = true });
        await modalEjecutar.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modalEjecutar.Locator(".modal-pie").GetByRole(AriaRole.Button, new() { Name = "Destruir definitivamente", Exact = true }).ClickAsync();
        await EsperarCierreAsync(page, modalEjecutar);

        await AsegurarTextoEnFilaAsync(page, filaDocumentos, "Ejecutada el");

        // --- La otra propuesta se descarta en vez de ejecutarse — camino
        // alternativo, no todo lo detectado termina en destrucción ---
        await filaTrabajadores.GetByRole(AriaRole.Button, new() { Name = "Descartar", Exact = true }).ClickAsync();

        var modalDescartar = page.GetByRole(AriaRole.Dialog, new() { Name = "Descartar la propuesta", Exact = true });
        await modalDescartar.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await modalDescartar.GetByLabel("Motivo").FillAsync("E2E: la organización conserva estos registros por política interna (P1-19)");
        await modalDescartar.Locator(".modal-pie").GetByRole(AriaRole.Button, new() { Name = "Descartar", Exact = true }).ClickAsync();
        await EsperarCierreAsync(page, modalDescartar);

        await AsegurarTextoEnFilaAsync(page, filaTrabajadores, "Descartada");
        traza.Superada();
    }

    /// <summary>
    /// Regla de Chris del 2026-10-20 (opción A, PR #1488): el Administrador del Operador CAE externo
    /// entra en cada Tenant beneficiario que cubre una Asignación de Operación vigente, externa y del
    /// Tenant entero de su organización con su rol de origen y todos sus poderes, sin Encargo de
    /// administración. Las exclusiones de los encargos no plenos, Retención entre ellas, dejan de
    /// aplicarse. Deroga la regla anterior, por la que aquí entraba con el rol <b>GestorCae</b> de su
    /// delegación heredada y la puerta de <c>/retencion</c> lo mandaba a acceso denegado.
    ///
    /// <para>
    /// Lo que sigue cerrado es la frontera de la Asignación de Operación: sin ella no entra en el
    /// Tenant (<c>EncargoAdministracionE2ETests</c>). Este test no dispara ninguna purga: mide la
    /// puerta, y el ciclo completo lo recorre el primero.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_Administrador_del_Operador_CAE_externo_administra_la_retencion_del_Tenant_beneficiario_que_opera()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/retencion");

        // Evidencia positiva: la página de Retención del Tenant beneficiario, con el control que
        // abre el ciclo, y no la de acceso denegado.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Buscar datos que hayan cumplido plazo", Exact = true }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        Assert.DoesNotContain("/acceso-denegado", page.Url);
        await Assertions.Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync(
            "data-tenant-id",
            await fixture.LeerValorSqlAsync(
                """SELECT "Id"::text FROM "Tenants" WHERE "Nombre" = @nombre""", ("nombre", Ayudas.NombreClienteDelegadoDemo)));
    }

    /// <summary>
    /// El QuickGrid/tabla se reconstruye tras cada Command (nueva consulta a
    /// ObtenerSolicitudesPurgaQuery) — esperar el texto en vez de leer una
    /// sola vez evita una carrera con el re-render.
    ///
    /// <para>
    /// Si el texto no llega, el mensaje lleva el estado de la pantalla en ese momento (filas de la tabla y avisos),
    /// para que la próxima aparición de un intermitente diga qué se vio y no solo qué faltó (#2187).
    /// </para>
    /// </summary>
    private static async Task AsegurarTextoEnFilaAsync(IPage page, ILocator fila, string textoEsperado)
    {
        try
        {
            await fila.GetByText(textoEsperado).WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"«{textoEsperado}» no apareció en la fila en 10 s. {await DescribirPantallaAsync(page)}", ex);
        }
    }

    /// <summary>
    /// Espera a que el diálogo se cierre; si no se cierra, el mensaje lleva su texto, que es donde el
    /// <c>ModalFormulario</c> enseña el error del servidor o de validación (p. ej. «Indica una fecha válida.»).
    /// </summary>
    private static async Task EsperarCierreAsync(IPage page, ILocator modal)
    {
        try
        {
            await modal.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 10_000 });
        }
        catch (TimeoutException ex)
        {
            string textoModal;
            try
            {
                textoModal = string.Join(" | ", await modal.AllInnerTextsAsync());
            }
            catch (PlaywrightException errorLectura)
            {
                textoModal = $"no se pudo leer ({errorLectura.Message})";
            }

            throw new TimeoutException(
                $"El diálogo sigue abierto a los 10 s. Diálogo: [{textoModal}]. {await DescribirPantallaAsync(page)}", ex);
        }
    }

    /// <summary>
    /// Lectura sin espera (AllInnerTexts no aguarda a que exista nada): solo instrumenta el mensaje del fallo. Si la
    /// página ya no responde, lo dice en vez de lanzar, para no tapar el TimeoutException original.
    /// </summary>
    private static async Task<string> DescribirPantallaAsync(IPage page)
    {
        try
        {
            var filas = await page.Locator(".tabla-datos tbody tr").AllInnerTextsAsync();
            var avisos = await page.Locator(".anfitrion-toasts").AllInnerTextsAsync();
            return $"Filas de la tabla ({filas.Count}): [{string.Join(" | ", filas.Select(Compactar))}]. " +
                   $"Avisos: [{string.Join(" | ", avisos.Select(Compactar))}].";
        }
        catch (PlaywrightException ex)
        {
            return $"Pantalla no disponible para describirla ({ex.Message}).";
        }

        static string Compactar(string texto) => string.Join(' ', texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
