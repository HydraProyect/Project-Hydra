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
    /// Y — N cliente(s)»), pero el paginador ya solo aparece con más de una página, y la siembra
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
    /// Antes este test se llamaba "Administrador ve los 200 clientes
    /// sembrados" y comprobaba justo lo contrario: que ser Administrador en
    /// la Consultora bastaba para verlo todo dentro del Delegated Workspace.
    /// Eso era el hallazgo N-5 de Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md —
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
    //
    // Comprobado por mutación, no solo por verde: quitando el filtro de
    // alcance de ObtenerClientesQuery el test del administrador delegado se
    // pone en rojo (pasa de alcance cero a ver la cartera entera).
    [Fact]
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
        // sembrados y nadie le asigna cartera en el workspace delegado (ver
        // DelegacionDemoSeeder: le da rol, no clientes), así que como
        // GestorCae su cartera está vacía. Lo que se comprueba es que NO
        // aparece la cartera entera (9 clientes en la siembra determinista):
        // si el rol del claim volviera a filtrarse al workspace ajeno, el
        // contador saldría con el total y este test fallaría — verificado
        // por mutación el 2026-08-28, no supuesto.
        //
        // El contador de la cabecera se pinta también con cero, así que esta prueba ya no es de
        // un solo sentido: el total tiene que quedar por debajo de la cartera entera, y si es
        // cero (el camino esperado: alcance cero) se exige además el estado vacío propio —sin
        // esa exigencia, una /clientes rota por cualquier otro motivo pasaría por "acotada
        // correctamente", que es justo el falso verde que la cuarentena de F3b dejó vivo dos días.
        //
        // Con alcance cero, desde P0-9a (FS-05) el estado vacío ya no es «Aún no
        // hay clientes» —que invitaba a crear lo que existe fuera de su
        // cartera— sino el aviso «Sin Asignación de Cartera», y la cabecera
        // deja de ofrecer «+ Nuevo Cliente empresarial».
        var total = await LeerTotalDeLaCabeceraAsync(page);
        Assert.True(total < 9, $"el administrador delegado ve {total} Clientes empresariales: la cartera entera son 9");

        if (total == 0)
        {
            await Assertions.Expect(page.Locator("[data-estado=sin-asignacion-cartera]"))
                .ToContainTextAsync("Sin Asignación de Cartera", new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
            await Assertions.Expect(page.GetByText("Aún no hay Clientes empresariales")).Not.ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ Nuevo Cliente empresarial" }))
                .Not.ToBeVisibleAsync();
        }
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
        await Assertions.Expect(page.GetByText("+ Nuevo Cliente empresarial")).ToHaveCountAsync(0);
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
