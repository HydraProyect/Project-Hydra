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
    // La cuarentena de GestorCae_ve_solo_su_cartera_acotada (hoy
    // GestorCae_con_cartera_del_Tenant_entero_ve_todos_los_Clientes…) y
    // Consulta_ve_todo_pero_no_puede_crear_un_cliente (Fase 69: "cuelgue
    // intermitente sin causa identificada") se levantó al encontrar la causa
    // raíz: el rate limiting de /cuenta/* devolvía 429 al POST de login
    // cuando la suite acumulaba más de 10 POST anónimos por minuto desde
    // 127.0.0.1 — ver el comentario en WebAppFixture, que es donde vive el
    // arreglo (techo del limitador configurable para la suite).

    /// <summary>
    /// Total de Clientes empresariales que la lista dice tener: el contador junto al título
    /// (CabeceraListado, rediseño de listados fase 1). Antes se leía del paginador («Página X de
    /// Y — N cliente(s)»). El pie actual usa «Mostrando N de M» incluso en una sola página; la siembra
    /// determinista tiene 9. El contador solo se pinta con la carga terminada sin error, así que
    /// esperarlo es esperar a que la consulta haya respondido.
    /// </summary>
    private static async Task<int> LeerTotalDeLaCabeceraAsync(IPage page)
    {
        var contador = page.Locator(".cabecera-listado-contador");
        await contador.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        return int.Parse((await contador.InnerTextAsync()).Trim());
    }

    /// <summary>
    /// Regla de Chris del 2026-10-20 (opción A, PR #1488): el usuario del Operador CAE externo tiene
    /// su rol de origen, con todos sus poderes, en cada Tenant beneficiario que cubre una Asignación
    /// de Operación vigente, externa y del Tenant entero de su organización. Solo el Gestor CAE sigue
    /// acotado por su Asignación de Cartera. El rol que guarda la delegación heredada
    /// (<c>AsignacionOperadorDelegado.Rol</c>, <c>DelegacionDemoSeeder.RolOperadorDelegadoDemo</c>) ya
    /// no decide: deroga la regla anterior, por la que el Administrador del Operador CAE externo
    /// entraba aquí como <c>GestorCae</c> con cartera vacía.
    ///
    /// <para>
    /// La frontera que sigue en pie es la Asignación de Operación: sin ella no hay entrada (lo
    /// comprueba <c>EncargoAdministracionE2ETests</c> con un Tenant que la organización no opera).
    /// </para>
    ///
    /// El Administrador que opera el Tenant beneficiario es el de la Consultora de la demo, no
    /// <c>admin@caemanager.local</c>: desde el 2026-08-14 la cuenta de plataforma no opera ningún
    /// Tenant delegado (ver DelegacionDemoSeeder).
    /// </summary>
    // F3b (2026-08-26) puso en cuarentena las pruebas de visibilidad acotada
    // de más abajo: leían el contador del paginador de /clientes, y con
    // ObtenerClientesQuery congelada sobre la tabla legacy Clientes esa
    // pantalla salía vacía en cualquier entorno — el contador ni se
    // renderizaba, así que el instrumento no podía observar nada.
    //
    // F4 (2026-08-27, #288/#291) levanta esa condición: ObtenerClientesQuery
    // lee Empresas con EsCritico != null y SIGUE aplicando el filtro de
    // IAlcanceDatosService, y AsignacionesOperativasBackfillSeeder emite las
    // carteras con ámbito Empresa.Id — los dos lados del Contains hablan del
    // mismo identificador, que es lo que hacía falta para que el filtro
    // recorte en vez de vaciar. Cuarentena levantada el 2026-08-28.
    [Fact]
    public async Task El_Administrador_del_Operador_CAE_externo_ve_el_Tenant_beneficiario_entero_con_su_rol_de_origen()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);

        // El tenant de origen del Administrador (Consultora, ADR-004 § 5.1)
        // no tiene datos operativos propios — los ~200 Clientes sembrados de
        // prueba viven en su Delegated Workspace (ver DelegacionDemoSeeder).
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        // El administrador no es la referencia de ninguno de los Clientes empresariales sembrados ni
        // tiene Asignación de Cartera en este Tenant: lo que le abre el Tenant entero es su rol de
        // origen bajo la Asignación de Operación. La siembra determinista tiene 9; otros tests de la
        // colección pueden añadir alguno, así que se exige al menos la cartera entera, no el número
        // exacto. Con la regla derogada salía 0 y el aviso «Sin Asignación de Cartera».
        var total = await LeerTotalDeLaCabeceraAsync(page);
        Assert.True(total >= 9, $"el Administrador del Operador CAE externo ve {total} Clientes: el Tenant entero son al menos 9");

        await Assertions.Expect(page.Locator("[data-estado=sin-asignacion-cartera]")).ToHaveCountAsync(0);
    }

    /// <summary>
    /// La cartera de un GestorCae es el Tenant entero (D-7, 2026-10-02): con su Asignación de Cartera
    /// vigente ve todos los Clientes empresariales del Tenant, no solo aquellos de los que es la
    /// referencia (<c>Empresa.EjecutivoUsuarioId</c>). El primer usuario de prueba de este rol es la
    /// referencia de 3 de los 9 Clientes sembrados (ver DatosPruebaSeeder: reparto round-robin entre 3
    /// gestores), y aun así ve los 9: la siembra le concede la cartera del Tenant entero de forma
    /// explícita (CarterasDeSiembra). Antes de D-7 este test fijaba que veía exactamente 3.
    /// </summary>
    [Fact]
    public async Task GestorCae_con_cartera_del_Tenant_entero_ve_todos_los_Clientes_y_no_solo_aquellos_de_los_que_es_la_referencia()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        Assert.Equal(9, await LeerTotalDeLaCabeceraAsync(page));
    }

    [Fact]
    public async Task Consulta_ve_todo_pero_no_puede_crear_un_cliente()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("consulta", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        // La mitad "ve todo" del nombre: F3b/D2 la retiró en 2026-08-26 porque
        // /clientes salía vacío para todos, y el test se quedó probando solo
        // el bloqueo de escritura — un nombre que prometía más que su
        // contrato efectivo. F4 (#288/#291) devuelve el contador, así que la
        // comprobación vuelve: Consulta es rol de alcance total
        // (TieneAccesoTotalAsync), luego ve los 9 clientes de la siembra
        // determinista, no un subconjunto.
        Assert.Equal(9, await LeerTotalDeLaCabeceraAsync(page));

        // Desde la demo a dirección (2026-09-20) la interfaz ya no ofrece lo que el rol
        // no puede hacer: antes «+ Nuevo Cliente empresarial» se veía habilitado y fallaba al guardar
        // con «Tu rol no permite crear, editar ni eliminar datos». Ahora ni se ofrece —ni
        // en la cabecera ni en el EstadoVacio— y una franja dice que es modo solo consulta.
        // La denegación de verdad (AutorizacionEscrituraBehavior, «Autorizacion.SoloLectura»)
        // sigue siendo la que decide, y la vigila RolesConEscrituraParidadTests contra la
        // lista que usa la interfaz.
        await Assertions.Expect(page.GetByText("+ Nuevo Cliente")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".aviso-solo-consulta")).ToBeVisibleAsync();
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
