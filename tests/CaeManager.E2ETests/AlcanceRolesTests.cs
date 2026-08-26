using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Adapta a Playwright .NET los chequeos de alcance por rol que hasta ahora
/// se verificaban a mano con el script de Node (ver
/// /tmp/.../scratchpad/verificar_roles.js) — qué ve y qué no ve cada uno de
/// los 6 roles (ver Roles.cs). Cada test usa su propio IBrowserContext/IPage
/// en vez de compartir una página y hacer login/logout entre roles: el
/// propio script original advertía que el logout entre usuarios no era
/// fiable, así que aquí se evita esa clase de flakiness desde el diseño.
/// </summary>
[Collection("AppCollection")]
public partial class AlcanceRolesTests(WebAppFixture fixture)
{
    // La cuarentena de GestorCae_ve_solo_su_cartera_acotada y
    // Consulta_ve_todo_pero_no_puede_crear_un_cliente (Fase 69: "cuelgue
    // intermitente sin causa identificada") se levantó al encontrar la causa
    // raíz: el rate limiting de /cuenta/* devolvía 429 al POST de login
    // cuando la suite acumulaba más de 10 POST anónimos por minuto desde
    // 127.0.0.1 — ver el comentario en WebAppFixture, que es donde vive el
    // arreglo (techo del limitador configurable para la suite).

    [GeneratedRegex(@"Página \d+ de \d+")]
    private static partial Regex PatronContadorElementos();

    [GeneratedRegex(@"—\s*(\d+)\s")]
    private static partial Regex PatronTotalElementos();

    /// <summary>
    /// El paginador único en español (H2, docs/ux-audit/02-clientes.md;
    /// UX_PATTERNS.md § Paginar) renderiza "Página X de Y — N cliente(s)" —
    /// antes del cambio de <c>Paginator</c> de QuickGrid ("1–20 of 200
    /// items") este método buscaba el número justo antes de "items". El
    /// total ahora está justo después del guion largo, delante de la
    /// etiqueta de la entidad.
    /// </summary>
    private static int ExtraerTotalElementos(string textoPaginador)
    {
        var coincidencia = PatronTotalElementos().Match(textoPaginador);
        if (!coincidencia.Success)
            throw new InvalidOperationException($"No se encontró un total de elementos en «{textoPaginador}».");

        return int.Parse(coincidencia.Groups[1].Value);
    }

    /// <summary>
    /// Antes este test se llamaba "Administrador ve los 200 clientes
    /// sembrados" y comprobaba justo lo contrario: que ser Administrador en
    /// la Consultora bastaba para verlo todo dentro del Delegated Workspace.
    /// Eso era el hallazgo N-5 de INFORME-AUDITORIA-2.md —
    /// <c>AsignacionOperadorDelegado.Rol</c> se guardaba y no se leía nunca—,
    /// así que el test estaba fijando el defecto.
    ///
    /// Desde que el rol efectivo lo decide la asignación (ADR-004 § 5.3), el
    /// administrador entra al workspace de demo como <c>GestorCae</c>
    /// (<c>DelegacionDemoSeeder.RolOperadorDelegadoDemo</c>) y solo ve la
    /// cartera de ese rol, no los ~200 clientes del Cliente Delegante.
    ///
    /// Que ningún Operador Delegado alcance acceso total es deliberado:
    /// <c>CrearAsignacionOperadorDelegadoCommandValidator</c> excluye
    /// Administrador y DireccionCae de los roles asignables — se opera dentro
    /// del alcance del workspace, nunca con privilegios de administración de
    /// la plataforma del cliente.
    ///
    /// El Administrador que opera el Delegated Workspace es el de ArcoSPA
    /// (la Consultora de la demo), no <c>admin@caemanager.local</c> — desde
    /// el 2026-08-14 la cuenta de plataforma no opera ningún Delegated
    /// Workspace (ver DelegacionDemoSeeder).
    /// </summary>
    // F3b (2026-08-26): las tres pruebas de más abajo verificaban visibilidad
    // acotada por cartera (GestorCae/Consulta/Administrador delegado) leyendo
    // el contador del paginador de /clientes, respaldado por
    // ObtenerClientesQuery — una de las 6 consultas que D2 deja leyendo la
    // tabla legacy Clientes hasta F4. Con los escritores de Cliente
    // redirigidos a Empresa, esa pantalla queda vacía en cualquier entorno
    // (decisión explícita: "aceptar el vacío", ver
    // f3b-decision-d2-transicion-acotada-2026-08-25.md): el contador ya ni
    // siquiera se renderiza (Clientes.razor muestra el EstadoVacio en su
    // lugar), así que el instrumento no puede observar la propiedad que
    // pretendía probar — ni "true" ni "false" tendría el significado que
    // tenía antes. La propiedad de fondo (alcance por cartera vía
    // AlcanceDatosService) sigue demostrada en
    // CaeManager.IntegrationTests.MemoizacionAlcanceDatosTests y
    // CaeManager.IntegrationTests.Tenants.RolEfectivoEnDelegacionTests, que
    // sí pueden observarla directamente contra el filtro real, así que no
    // queda un hueco de cobertura de la propiedad — solo de esta ruta E2E
    // concreta, que se retoma cuando F4 resuelva ObtenerClientesQuery.
    [Fact(Skip = "F3b/D2: ObtenerClientesQuery congelada, /clientes vacío hasta F4 — ver f3b-decision-d2-transicion-acotada-2026-08-25.md. Propiedad cubierta en IntegrationTests (MemoizacionAlcanceDatosTests, RolEfectivoEnDelegacionTests).")]
    public async Task El_rol_de_la_delegacion_acota_al_administrador_dentro_del_workspace_delegado()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);

        // El tenant de origen del Administrador (Consultora, ADR-004 § 5.1)
        // no tiene datos operativos propios — los ~200 Clientes sembrados de
        // prueba viven en su Delegated Workspace (ver DelegacionDemoSeeder).
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        // El administrador no es ejecutivo de ninguno de los clientes
        // sembrados, así que como GestorCae su cartera está vacía. Lo que se
        // comprueba es que NO aparece la cartera entera (9 clientes en la
        // siembra determinista): si el rol del claim volviera a filtrarse al
        // workspace ajeno, el contador saldría con el total y este test
        // fallaría.
        var contador = page.GetByText(PatronContadorElementos()).First;
        var totalVisible = await contador.IsVisibleAsync();

        if (totalVisible)
            Assert.True(ExtraerTotalElementos(await contador.InnerTextAsync()) < 9);
    }

    /// <summary>
    /// GestorCae solo ve su propia cartera (Cliente.EjecutivoUsuarioId) — el
    /// primer usuario de prueba de este rol tiene exactamente 3 de los 9
    /// clientes sembrados (ver DatosPruebaSeeder, "cartera de prueba": todos
    /// los clientes repartidos entre 3 gestores round-robin). Se comprueba el
    /// reparto exacto porque la siembra es determinista — no es un número
    /// arbitrario.
    /// </summary>
    [Fact(Skip = "F3b/D2: ObtenerClientesQuery congelada, /clientes vacío hasta F4 — ver f3b-decision-d2-transicion-acotada-2026-08-25.md. Propiedad cubierta en IntegrationTests (MemoizacionAlcanceDatosTests, RolEfectivoEnDelegacionTests).")]
    public async Task GestorCae_ve_solo_su_cartera_acotada()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        var contador = page.GetByText(PatronContadorElementos()).First;
        await contador.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        var total = ExtraerTotalElementos(await contador.InnerTextAsync());
        Assert.Equal(3, total);
    }

    [Fact]
    public async Task Consulta_ve_todo_pero_no_puede_crear_un_cliente()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("consulta", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        // F3b/D2 (2026-08-26): la comprobación de visibilidad total (9
        // clientes de la siembra determinista) se retira — ObtenerClientesQuery
        // sigue congelada hasta F4 y /clientes queda vacío en cualquier
        // entorno (ver f3b-decision-d2-transicion-acotada-2026-08-25.md), así
        // que el contador del paginador ni siquiera se renderiza. La
        // propiedad de visibilidad total de Consulta está cubierta en
        // IntegrationTests (AlcanceDatosService); lo que este test verifica
        // de forma real y sigue observable aquí es el bloqueo de escritura,
        // abajo. .First: con la lista vacía, el botón "+ Nuevo cliente"
        // aparece tanto en la cabecera como en el EstadoVacio.
        await page.GetByText("+ Nuevo cliente").First.ClickAsync();
        var drawer = page.Locator(".drawer-panel");
        await drawer.GetByLabel("Razón social").FillAsync("Cliente bloqueado por rol Consulta");
        await drawer.GetByLabel("CIF", new LocatorGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_888_801));
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();

        // AutorizacionEscrituraBehavior bloquea cualquier Command para
        // Consulta/Cliente con el error "Autorizacion.SoloLectura" (ver esa
        // clase en CaeManager.Application.Common) — se muestra en ".alerta-formulario".
        var alerta = drawer.Locator(".alerta-formulario");
        await alerta.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        Assert.Contains("no permite", (await alerta.InnerTextAsync()).ToLowerInvariant());

        // El drawer sigue abierto: el bloqueo no debe perder lo ya escrito.
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 2_000 });
    }

    [Fact]
    public async Task Rol_Cliente_ve_un_menu_reducido_a_lo_que_puede_consultar_de_si_mismo()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("cliente", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/");

        var nav = page.Locator(".nav-principal");
        await nav.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        var textoNav = await nav.InnerTextAsync();

        Assert.DoesNotContain("Asignaciones", textoNav);
        Assert.DoesNotContain("Vehículos", textoNav);
        Assert.DoesNotContain("Visitas", textoNav);
        Assert.DoesNotContain("Administración", textoNav);
        Assert.DoesNotContain("Usuarios", textoNav);

        Assert.Contains("Empresas", textoNav);
        Assert.Contains("Trabajadores", textoNav);
        Assert.Contains("Documentos", textoNav);
    }
}
