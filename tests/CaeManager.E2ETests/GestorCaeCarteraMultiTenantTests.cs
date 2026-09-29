using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// El recorrido del piloto Outbound entrando COMO Gestor CAE de un Operador CAE
/// externo —no como Administrador— con una Asignación de Cartera vigente en dos
/// Tenants beneficiarios (ver GestorCaeCarteraMultiTenantSeeder): Mi trabajo con
/// filas de los dos, abrir una fila del Tenant beneficiario que no está activo (el
/// formulario real de la fila hace el POST a /cuenta/cliente-activo), corregir la
/// acreditación Rechazada, crear y cancelar una Visita, anotar a mano la vigencia en
/// plataforma y comprobar al volver que esas filas ya no están. Y los negativos: el
/// Tenant beneficiario que el mismo Operador CAE externo opera pero que queda fuera de
/// la cartera no aparece en ningún sitio ni se abre por enlace directo. Ese Tenant no
/// lleva tampoco AsignacionOperadorDelegado del Gestor CAE: con ella, la vía heredada de
/// /cuenta/cliente-activo lo autorizaría sin cartera (alcance cero), y esa regla la fija
/// el selector de Tenant, no este recorrido. La frontera de cartera dentro de un Tenant
/// operado la cubre el Cliente empresarial sin cartera del primer Tenant beneficiario.
///
/// <para>
/// Los nombres de abajo duplican los de la siembra a propósito: este proyecto no
/// referencia Infrastructure (arranca el binario real), igual que <see cref="Ayudas"/>.
/// </para>
/// </summary>
[Collection("AppCollectionGestorCaeCarteraMultiTenant")]
public class GestorCaeCarteraMultiTenantTests(WebAppFixtureGestorCaeCarteraMultiTenant fixture)
{
    private const string EmailGestorCae = "gestor.cartera.e2e@caemanager.local";

    private const string TenantBeneficiarioA = "Conservas Albatros S.L. (Tenant beneficiario E2E)";
    private const string TenantBeneficiarioB = "Talleres Boreal S.A. (Tenant beneficiario E2E)";
    private const string TenantFueraDeCartera = "Minería Cierzo S.L. (fuera de cartera E2E)";

    private const string TrabajadorA = "Castany Olmo";
    private const string TrabajadorB = "Ledesma Pardo";
    private const string TrabajadorFueraDeCarteraEnA = "Vilches Roca";
    private const string NombreCompletoTrabajadorB = "Bruno Ledesma Pardo";

    private const string CentroA = "Nave Albatros Gijón";
    private const string CentroB = "Planta Boreal Burgos";
    private const string CentroFueraDeCarteraEnA = "Taller Vilches Lugo";
    private const string CentroFueraDeCartera = "Mina Cierzo Teruel";

    private const string BadgeRechazada = "Rechazada por plataforma";
    private const string BadgePendienteDeEnvio = "Pendiente de envío";
    private const string BadgeVencidaEnPlataforma = "Vencida en plataforma";
    private const string BadgeProximo = "Próximo";

    /// <summary>
    /// Primera espera de cada test: puede caer sobre la fixture recién arrancada
    /// (primer circuito, primera consulta agregada de Mi trabajo), donde los 5 s por
    /// defecto no bastan — la firma de un fallo así es un [1 ms] engañoso en el .trx.
    /// </summary>
    private static readonly LocatorAssertionsToBeVisibleOptions EsperaEnFrio = new() { Timeout = 30_000 };

    /// <summary>
    /// Lote 2 del selector de empresa gestionada: seleccionar → Trabajadores. La cabecera de la
    /// lista dice de qué empresa es, el cambio vuelve a la misma ruta SIN query (los filtros del
    /// Tenant anterior se descartan, I14) y la selección persiste al navegar (cookie, no estado de
    /// pantalla).
    /// </summary>
    [Fact]
    public async Task El_Gestor_CAE_cambia_de_empresa_gestionada_y_Trabajadores_lleva_la_cabecera_de_la_elegida_sin_filtros_del_anterior()
    {
        var tenantA = await IdTenantAsync(TenantBeneficiarioA);
        var tenantB = await IdTenantAsync(TenantBeneficiarioB);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, EmailGestorCae, Ayudas.ContrasenaUsuariosPrueba);

        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, TenantBeneficiarioA);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores?q={TrabajadorA}");
        await Expect(page.Locator(".trabajadores-empresa")).ToContainTextAsync(TenantBeneficiarioA, new() { Timeout = 30_000 });
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync("data-tenant-id", tenantA);

        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, TenantBeneficiarioB);
        await page.WaitForURLAsync(url => new Uri(url).PathAndQuery == "/trabajadores");
        await Expect(page.Locator(".trabajadores-empresa")).ToContainTextAsync(TenantBeneficiarioB, new() { Timeout = 30_000 });
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync("data-tenant-id", tenantB);

        // La selección sobrevive a navegar a otra pantalla y volver a Trabajadores.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await Expect(page.Locator(".trabajadores-empresa")).ToContainTextAsync(TenantBeneficiarioB, new() { Timeout = 30_000 });
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync("data-tenant-id", tenantB);
    }

    [Fact]
    public async Task El_Gestor_CAE_recorre_su_cartera_en_dos_Tenants_beneficiarios_y_resuelve_la_cola()
    {
        // Día de negocio (Madrid), como la siembra (el seeder GestorCaeCarteraMultiTenantSeeder usa
        // DiaDeNegocio.Hoy()): con el día UTC, entre las 22:00 y las 24:00 UTC «mañana»
        // (la Visita sembrada) y «hoy» no serían los mismos días en los dos lados.
        var hoy = Ayudas.HoyDeNegocio();
        var tenantB = await IdTenantAsync(TenantBeneficiarioB);
        var rechazadaB = await IdAcreditacionAsync(TrabajadorB, estado: 3);
        var vencidaEnPlataformaB = await IdAcreditacionAsync(TrabajadorB, estado: 2);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, EmailGestorCae, Ayudas.ContrasenaUsuariosPrueba);

        // Con más de una organización autorizada, el menú lleva a la cola de toda la cartera.
        await Expect(page.Locator("nav a.nav-item", new() { HasText = "Mi trabajo" })).ToHaveAttributeAsync("href", "mi-trabajo");

        // El Tenant beneficiario A queda activo con el selector real; B es el no activo.
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, TenantBeneficiarioA);
        Assert.NotEqual(tenantB, await Ayudas.TenantActivoIdAsync(page));

        // Mi trabajo: filas de los dos Tenants beneficiarios en la misma cola.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/mi-trabajo");
        await FiltrarColaAsync(page, TrabajadorA, esperaEnFrio: true);
        await Expect(FilaMiTrabajo(page, TenantBeneficiarioA, BadgeRechazada)).ToBeVisibleAsync();
        await FiltrarColaAsync(page, TrabajadorB);
        var filaRechazadaB = FilaMiTrabajo(page, TenantBeneficiarioB, BadgeRechazada);
        await Expect(filaRechazadaB).ToBeVisibleAsync();
        await Expect(FilaMiTrabajo(page, TenantBeneficiarioB, BadgeVencidaEnPlataforma)).ToBeVisibleAsync();
        await Expect(FilaMiTrabajo(page, TenantBeneficiarioB, BadgeProximo)).ToBeVisibleAsync();
        await FiltrarColaAsync(page, CentroB);
        await Expect(FilaMiTrabajo(page, TenantBeneficiarioB, "Visita próxima")).ToBeVisibleAsync();

        // Abrir la fila Rechazada de B, que no es el Tenant activo: el formulario de la
        // propia fila (AccionCrossTenant) hace el POST real y aterriza en la acreditación.
        await FiltrarColaAsync(page, TrabajadorB);
        var destinoEsperado = $"/documentos?pestana=plataforma&acreditacionId={rechazadaB}";
        var cambio = await page.RunAndWaitForResponseAsync(
            () => filaRechazadaB.Locator("form[data-accion-cross-tenant] button[type=submit]").ClickAsync(),
            r => r.Url.Contains("/cuenta/cliente-activo") && r.Request.Method == "POST");
        Assert.InRange(cambio.Status, 300, 399);
        Assert.Equal(destinoEsperado, cambio.Headers.GetValueOrDefault("location"));
        await page.WaitForURLAsync(url => url.EndsWith(destinoEsperado, StringComparison.Ordinal));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync("data-tenant-id", tenantB);

        // Corregir la Rechazada: versión corregida, que la devuelve a pendiente de envío.
        var filaAcreditacion = page.Locator($".plataforma-fila-documento[data-acreditacion-id='{rechazadaB}']");
        await filaAcreditacion.GetByRole(AriaRole.Button, new() { Name = "Subir versión corregida", Exact = true }).ClickAsync();
        var drawer = page.Locator(".drawer-panel");
        await Expect(drawer.GetByText("Renovar documento")).ToBeVisibleAsync();
        await drawer.GetByLabel("Fecha de emisión", new() { Exact = true }).FillAsync(hoy.AddDays(-1).ToString("yyyy-MM-dd"));
        await drawer.Locator("input[type=\"file\"]").SetInputFilesAsync(Ayudas.GenerarPdfDePruebaEnDisco("formacion-corregida.pdf"));
        await drawer.GetByText("Archivo adjuntado correctamente.").WaitForAsync();
        await drawer.Locator(".drawer-pie").GetByRole(AriaRole.Button, new() { Name = "Guardar", Exact = true }).ClickAsync();
        await drawer.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await Expect(filaAcreditacion.GetByRole(AriaRole.Button, new() { Name = "Marcar subido", Exact = true })).ToBeVisibleAsync();
        await Expect(filaAcreditacion.GetByRole(AriaRole.Button, new() { Name = "Subir versión corregida", Exact = true })).ToHaveCountAsync(0);

        // Visita: crearla en el Centro de B y cancelarla.
        var fechaVisita = hoy.AddDays(10);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/visitas");
        await page.GetByRole(AriaRole.Button, new() { Name = "+ Nueva visita", Exact = true }).ClickAsync();
        var drawerVisita = page.GetByRole(AriaRole.Dialog, new() { Name = "Nueva visita", Exact = true });
        await Expect(drawerVisita).ToBeVisibleAsync();
        var selectCentro = drawerVisita.GetByLabel("Centro", new() { Exact = true });
        var valorCentro = await selectCentro.Locator("option", new() { HasText = CentroB }).GetAttributeAsync("value");
        await selectCentro.SelectOptionAsync(new SelectOptionValue { Value = valorCentro! });
        await drawerVisita.GetByLabel("Fecha de inicio", new() { Exact = true }).FillAsync(fechaVisita.ToString("yyyy-MM-dd"));
        await drawerVisita.GetByLabel("Fecha de fin", new() { Exact = true }).FillAsync(fechaVisita.ToString("yyyy-MM-dd"));
        // CrearVisitaCommandValidator exige al menos un Trabajador que entre.
        await drawerVisita.GetByLabel(NombreCompletoTrabajadorB, new() { Exact = true }).CheckAsync();
        await drawerVisita.GetByRole(AriaRole.Button, new() { Name = "Guardar", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Visita creada correctamente.")).ToBeVisibleAsync();

        var filaVisita = page.Locator("tr", new() { HasText = CentroB }).Filter(new() { HasText = fechaVisita.ToString("dd/MM/yyyy") });
        await Expect(filaVisita).ToHaveCountAsync(1);
        await Ayudas.PulsarAccionDeMenuAsync(filaVisita.Locator(".menu-acciones-disparador"), "Cancelar visita");
        var confirmacion = page.GetByRole(AriaRole.Dialog, new() { Name = $"¿Cancelar la visita a {CentroB}?", Exact = true });
        await confirmacion.GetByRole(AriaRole.Button, new() { Name = "Cancelar visita", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Visita cancelada.", new() { Exact = true })).ToBeVisibleAsync();
        // Con «Solo activas» (marcado por defecto) la cancelada sale del listado. Barrera
        // antes de la ausencia: la Visita sembrada de mañana, en el mismo Centro, sigue.
        await Expect(page.Locator("tr", new() { HasText = CentroB }).Filter(new() { HasText = hoy.AddDays(1).ToString("dd/MM/yyyy") }))
            .ToBeVisibleAsync();
        await Expect(filaVisita).ToHaveCountAsync(0);

        // Vigencia en plataforma anotada a mano: la acreditación aceptada deja de estar vencida allí.
        await Ayudas.NavegarYEsperarAsync(
            page, $"{fixture.BaseUrl}/documentos?pestana=plataforma&acreditacionId={vencidaEnPlataformaB}");
        var filaVencida = page.Locator($".plataforma-fila-documento[data-acreditacion-id='{vencidaEnPlataformaB}']");
        await filaVencida.GetByRole(AriaRole.Button, new() { Name = "Anotar vigencia…", Exact = true }).ClickAsync();
        var dialogoVigencia = page.GetByRole(AriaRole.Dialog, new() { Name = "Anotar vigencia", Exact = true });
        await dialogoVigencia.GetByLabel("¿Hasta cuándo vale en esta plataforma?", new() { Exact = true })
            .SelectOptionAsync(new SelectOptionValue { Value = "VenceEnFecha" });
        await dialogoVigencia.GetByLabel("Fecha de vencimiento", new() { Exact = true }).FillAsync(hoy.AddMonths(6).ToString("yyyy-MM-dd"));
        await dialogoVigencia.GetByRole(AriaRole.Button, new() { Name = "Guardar vigencia", Exact = true }).ClickAsync();
        await Expect(dialogoVigencia).ToBeHiddenAsync();

        // De vuelta en Mi trabajo: las dos filas resueltas ya no están. Barrera antes de
        // la ausencia: las filas que siguen vivas del mismo Trabajador —la corregida, ahora
        // pendiente de envío, y el documento a punto de vencer— ya están pintadas.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/mi-trabajo");
        await FiltrarColaAsync(page, TrabajadorB, esperaEnFrio: true);
        await Expect(FilaMiTrabajo(page, TenantBeneficiarioB, BadgePendienteDeEnvio)).ToBeVisibleAsync();
        await Expect(FilaMiTrabajo(page, TenantBeneficiarioB, BadgeProximo)).ToBeVisibleAsync();
        await Expect(FilaMiTrabajo(page, TenantBeneficiarioB, BadgeRechazada)).ToHaveCountAsync(0);
        await Expect(FilaMiTrabajo(page, TenantBeneficiarioB, BadgeVencidaEnPlataforma)).ToHaveCountAsync(0);

        // La cola sigue siendo de toda la cartera: la Rechazada de A no se ha tocado.
        await FiltrarColaAsync(page, TrabajadorA);
        await Expect(FilaMiTrabajo(page, TenantBeneficiarioA, BadgeRechazada)).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Lo_que_queda_fuera_de_la_cartera_no_aparece_ni_se_abre_por_enlace_directo()
    {
        var tenantA = await IdTenantAsync(TenantBeneficiarioA);
        var tenantFuera = await IdTenantAsync(TenantFueraDeCartera);
        var centroA = await IdCentroAsync(CentroA);
        var centroFueraEnA = await IdCentroAsync(CentroFueraDeCarteraEnA);
        var centroFuera = await IdCentroAsync(CentroFueraDeCartera);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, EmailGestorCae, Ayudas.ContrasenaUsuariosPrueba);

        // Selector de organización: los dos Tenants beneficiarios de la cartera, no el tercero.
        await Ayudas.AbrirSelectorTenantAsync(page);
        var opciones = Ayudas.OpcionesSelectorTenant(page);
        await Expect(opciones.Filter(new() { HasText = TenantBeneficiarioA })).ToHaveCountAsync(1);
        await Expect(opciones.Filter(new() { HasText = TenantBeneficiarioB })).ToHaveCountAsync(1);
        await Expect(opciones.Filter(new() { HasText = TenantFueraDeCartera })).ToHaveCountAsync(0);
        await Ayudas.CerrarSelectorTenantAsync(page);

        // Mi trabajo: el panel de cartera lista A y B (barrera) y no el Tenant de fuera.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/mi-trabajo");
        var cartera = page.Locator("aside.mi-trabajo-cartera .mi-trabajo-cartera-nombre");
        await Expect(cartera.Filter(new() { HasText = TenantBeneficiarioA })).ToBeVisibleAsync(EsperaEnFrio);
        await Expect(cartera.Filter(new() { HasText = TenantBeneficiarioB })).ToBeVisibleAsync();
        await Expect(cartera.Filter(new() { HasText = TenantFueraDeCartera })).ToHaveCountAsync(0);
        await Expect(page.Locator("div[role=button][aria-label$='· " + TenantFueraDeCartera + "']")).ToHaveCountAsync(0);

        // Dentro del Tenant beneficiario A, el Cliente empresarial sin Asignación de
        // Cartera tampoco llega a la cola. El mismo filtro, con un Trabajador de la
        // cartera, sí encuentra filas: el filtro no es lo que las esconde.
        await FiltrarColaAsync(page, TrabajadorA);
        await Expect(FilaMiTrabajo(page, TenantBeneficiarioA, BadgeRechazada)).ToBeVisibleAsync();
        await FiltrarColaAsync(page, TrabajadorFueraDeCarteraEnA);
        await Expect(page.GetByText("Sin resultados para este filtro", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator(".mi-trabajo-grupos div[role=button]")).ToHaveCountAsync(0);

        // Enlace directo con A activo: su Centro de cartera se abre (control positivo del
        // instrumento); el del Tenant de fuera da EstadoVacio, sin su nombre en pantalla.
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, TenantBeneficiarioA);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros/{centroA}");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = CentroA })).ToBeVisibleAsync();

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros/{centroFuera}");
        await Expect(page.GetByText("No pudimos cargar este centro", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText(CentroFueraDeCartera)).ToHaveCountAsync(0);

        // Y el Centro del Cliente empresarial sin cartera, dentro del propio Tenant activo.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros/{centroFueraEnA}");
        await Expect(page.GetByText("No pudimos cargar este centro", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText(CentroFueraDeCarteraEnA)).ToHaveCountAsync(0);

        // Activar el Tenant de fuera por el mismo endpoint que usa la cola: el servidor
        // no lo aplica y la organización activa sigue siendo A.
        var token = await Ayudas.TokenAntiforgeryAsync(page);
        var respuesta = await page.RunAndWaitForResponseAsync(
            () => page.EvaluateAsync(
                """
                ([tenant, token]) => {
                    const form = document.createElement("form");
                    form.method = "post";
                    form.action = "/cuenta/cliente-activo";
                    for (const [nombre, valor] of [["__RequestVerificationToken", token], ["tenantId", tenant], ["returnUrl", "/centros"]]) {
                        const campo = document.createElement("input");
                        campo.type = "hidden";
                        campo.name = nombre;
                        campo.value = valor;
                        form.appendChild(campo);
                    }
                    document.body.appendChild(form);
                    form.submit();
                }
                """, new[] { tenantFuera, token! }),
            r => r.Url.Contains("/cuenta/cliente-activo") && r.Request.Method == "POST");
        Assert.NotEqual("/centros", respuesta.Headers.GetValueOrDefault("location"));
        // El servidor deniega (Forbid) y el navegador sigue esa redirección a /acceso-denegado.
        // Esperar a que aterrice antes de navegar: un GotoAsync lanzado con la redirección en
        // vuelo se cruza con ella («Navigation ... is interrupted by another navigation»).
        await page.WaitForURLAsync("**/acceso-denegado**");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/mi-trabajo");
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync("data-tenant-id", tenantA);
    }

    /// <summary>
    /// Hallazgo del recorrido en staging del 2026-09-24: al cargar <c>/centros</c> por
    /// URL, el contexto volvía al Tenant de origen y el cambio de Tenant no sobrevivía a
    /// una recarga. La selección vive en una cookie que el POST emite y que
    /// <c>RevalidacionClienteActivoMiddleware</c> revalida en cada petición completa;
    /// cuando los dos usaban predicados distintos para la vía de Operación, la primera
    /// carga completa la revocaba (#961 los unificó). Aquí: B, que no es el Tenant por
    /// defecto (hay dos en cartera), sigue activo tras abrir <c>/centros</c> por URL y
    /// tras recargarla, y la lista enseña su Centro, no el de A.
    /// </summary>
    [Fact]
    public async Task El_Tenant_beneficiario_elegido_sobrevive_a_abrir_centros_por_URL_y_a_recargar()
    {
        var tenantB = await IdTenantAsync(TenantBeneficiarioB);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, EmailGestorCae, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, TenantBeneficiarioB);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync("data-tenant-id", tenantB);
        await Expect(page.GetByText(CentroB, new() { Exact = true })).ToBeVisibleAsync(EsperaEnFrio);
        await Expect(page.GetByText(CentroA, new() { Exact = true })).ToHaveCountAsync(0);

        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync("data-tenant-id", tenantB);
        await Expect(page.GetByText(CentroB, new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText(CentroA, new() { Exact = true })).ToHaveCountAsync(0);
    }

    /// <summary>
    /// P0 del piloto Outbound (2026-09-28): con B activo, abrir Trabajador 360 desde la
    /// lista devolvía el contexto al Tenant de origen con el aviso de acceso no vigente.
    /// </summary>
    [Fact]
    public async Task El_Tenant_beneficiario_elegido_sobrevive_a_abrir_las_fichas_360_desde_sus_listas()
    {
        var tenantB = await IdTenantAsync(TenantBeneficiarioB);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, EmailGestorCae, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, TenantBeneficiarioB);

        await RecorridoFichas360.RecorrerTodasAsync(page, fixture.BaseUrl, tenantB);
    }

    /// <summary>Fila de Mi trabajo de un Tenant beneficiario con ese badge (aria-label = «{Título} · {Tenant}»).</summary>
    private static ILocator FilaMiTrabajo(IPage page, string tenant, string badge) =>
        page.Locator($"div[role=button][aria-label$='· {tenant}']").Filter(new() { HasText = badge });

    /// <summary>
    /// Filtra la cola por texto. Una búsqueda activa anula los pliegues por Tenant
    /// (contrato § 5 de Mi trabajo), así que las filas buscadas se pintan aunque su
    /// grupo esté plegado. El valor escrito se comprueba: sin circuito interactivo el
    /// @oninput no llega y el filtro no se aplicaría.
    /// </summary>
    private static async Task FiltrarColaAsync(IPage page, string texto, bool esperaEnFrio = false)
    {
        var filtro = page.GetByLabel("Filtrar esta cola por trabajador, documento o centro", new() { Exact = true });
        if (esperaEnFrio)
            await Expect(filtro).ToBeVisibleAsync(EsperaEnFrio);
        await filtro.FillAsync(texto);
        await Expect(filtro).ToHaveValueAsync(texto);
    }

    private Task<string> IdTenantAsync(string nombre) =>
        fixture.LeerValorSqlAsync("""SELECT "Id"::text FROM "Tenants" WHERE "Nombre" = @n""", ("n", nombre));

    private Task<string> IdCentroAsync(string nombre) =>
        fixture.LeerValorSqlAsync("""SELECT "Id"::text FROM "Centros" WHERE "Nombre" = @n""", ("n", nombre));

    /// <param name="estado">EstadoAcreditacion: 2 = Aceptada, 3 = Rechazada.</param>
    private Task<string> IdAcreditacionAsync(string apellidosTrabajador, int estado) =>
        fixture.LeerValorSqlAsync(
            $"""
             SELECT a."Id"::text
             FROM "AcreditacionesDocumentoPlataforma" a
             JOIN "Documentos" d ON d."Id" = a."DocumentoId"
             JOIN "Trabajadores" t ON t."Id" = d."TrabajadorId"
             WHERE t."Apellidos" = @apellidos AND a."Estado" = {estado}
             """,
            ("apellidos", apellidosTrabajador));
}
