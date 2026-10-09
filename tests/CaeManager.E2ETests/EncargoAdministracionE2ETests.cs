using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Encargo de administración (decisión D-8, 2026-10-08) con un navegador real. El protagonista es
/// el Administrador del Operador CAE externo de demo, que tiene Asignación de Cartera vigente (rol
/// Gestor CAE) sobre dos Tenants propietarios: uno le ha encargado su administración
/// (<see cref="WebAppFixtureEncargoAdministracion.TenantConEncargo"/>) y el otro no
/// (<see cref="WebAppFixtureEncargoAdministracion.TenantSinEncargo"/>). La misma cuenta, la misma
/// cartera y el mismo rol en su Tenant de origen: lo único que cambia entre los dos es la fila del
/// encargo, que siembra la fixture.
///
/// <list type="number">
/// <item>Sin encargo no abre la configuración del Tenant propietario: opera con el techo de su
/// cartera.</item>
/// <item>Con encargo la abre, la cambia, y la pantalla le dice que administra por encargo.</item>
/// <item>Con encargo sigue sin poder crear una cuenta con rol Administrador de ese Tenant
/// propietario ni autorizar otro Operador CAE externo.</item>
/// </list>
///
/// <para>
/// Los tres recorridos aseguran el encargo antes de empezar, también el primero: que el Tenant
/// propietario sin encargo siga cerrado con el encargo del otro ya registrado es parte de lo que
/// se comprueba (el encargo no se contagia entre Tenants propietarios).
/// </para>
///
/// <para>
/// Los dos Tenants propietarios son de la siembra de demo y tienen Administrador propio. El
/// encargo está pensado para el que nace sin él, pero nada de lo que aquí se mide depende de eso:
/// el techo de rol mira la cartera, el encargo y el perfil de la cuenta en su Tenant de origen.
/// </para>
/// </summary>
[Collection("AppCollectionEncargoAdministracion")]
public class EncargoAdministracionE2ETests(WebAppFixtureEncargoAdministracion fixture)
{
    private const string TenantConEncargo = WebAppFixtureEncargoAdministracion.TenantConEncargo;
    private const string TenantSinEncargo = WebAppFixtureEncargoAdministracion.TenantSinEncargo;

    private const string RotuloUmbralProximo = "Umbral próximo (días)";
    private const string AccionAutorizarOperador = "Autorizar un Operador CAE externo";

    [Fact]
    public async Task Sin_encargo_el_Administrador_del_Operador_CAE_externo_no_abre_la_configuracion_del_Tenant_propietario()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await EntrarComoAdministradorDelOperadorAsync(contexto);

        await OperarAsync(page, TenantSinEncargo);

        // El servidor deniega la página (Forbid) y el navegador sigue la redirección. La URL y el
        // título de la página de acceso denegado son la evidencia positiva: no «no se ve el panel».
        foreach (var ruta in new[] { "/configuracion/params", "/tipos-documento" })
        {
            await page.GotoAsync($"{fixture.BaseUrl}{ruta}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            Assert.Contains("/acceso-denegado", page.Url);
            await Expect(page.GetByRole(AriaRole.Heading,
                    new PageGetByRoleOptions { Name = "No tienes permiso para ver esta sección", Exact = true }))
                .ToBeVisibleAsync();
        }

        // Sigue operando el Tenant propietario sin encargo: la denegación no lo devolvió a su origen.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/mi-trabajo");
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync(
            "data-tenant-id", await IdDeTenantAsync(TenantSinEncargo));
    }

    [Fact]
    public async Task Con_encargo_cambia_la_configuracion_del_Tenant_propietario_y_la_pantalla_dice_que_administra_por_encargo()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await EntrarComoAdministradorDelOperadorAsync(contexto);

        var umbralAntes = int.Parse(await LeerUmbralProximoAsync(TenantConEncargo));
        var umbralSinEncargoAntes = await LeerUmbralProximoAsync(TenantSinEncargo);
        var umbralOperadorAntes = await LeerUmbralProximoAsync(Ayudas.NombreTenantConsultora);
        var umbralNuevo = umbralAntes + 7;

        await OperarAsync(page, TenantConEncargo);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/configuracion/params");
        Assert.DoesNotContain("/acceso-denegado", page.Url);

        // La pantalla lo dice: su perfil de Administrador aquí es prestado.
        var aviso = page.GetByTestId("aviso-por-encargo");
        await Expect(aviso).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Expect(aviso).ToContainTextAsync("Administras esta organización por encargo");

        // El panel cargó con el valor del Tenant propietario, no con el de su Tenant de origen.
        var campo = page.GetByLabel(RotuloUmbralProximo, new PageGetByLabelOptions { Exact = true });
        await Expect(campo).ToHaveValueAsync(
            umbralAntes.ToString(), new LocatorAssertionsToHaveValueOptions { Timeout = 15_000 });

        // El presupuesto de IA es de los actos que el encargo no abre: no se le ofrece.
        await Expect(page.GetByTestId("presupuesto-ia-reservado")).ToBeVisibleAsync();

        await campo.FillAsync(umbralNuevo.ToString());
        await campo.BlurAsync();
        await page.Locator(".tarjeta-configuracion").Filter(new LocatorFilterOptions { Has = campo })
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Guardar cambios", Exact = true })
            .ClickAsync();

        // El cambio llegó a la base, en la fila del Tenant propietario.
        await EsperarUmbralProximoAsync(TenantConEncargo, umbralNuevo.ToString());

        // Y solo en esa: ni el otro Tenant propietario ni el Tenant de origen del Operador CAE.
        Assert.Equal(umbralSinEncargoAntes, await LeerUmbralProximoAsync(TenantSinEncargo));
        Assert.Equal(umbralOperadorAntes, await LeerUmbralProximoAsync(Ayudas.NombreTenantConsultora));

        // Tras recargar, la pantalla lo lee de vuelta.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/configuracion/params");
        await Expect(page.GetByLabel(RotuloUmbralProximo, new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(
            umbralNuevo.ToString(), new LocatorAssertionsToHaveValueOptions { Timeout = 15_000 });
    }

    [Fact]
    public async Task Con_encargo_no_crea_una_cuenta_Administrador_ni_autoriza_otro_Operador_CAE_externo()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await EntrarComoAdministradorDelOperadorAsync(contexto);

        // Control positivo, en su propia organización: ahí autorizar un Operador CAE externo sí
        // existe. Sin esto, «el botón no está» bajo encargo no distinguiría la regla de un rótulo
        // mal escrito en el test.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/delegaciones");
        await EsperarDelegacionesCargadasAsync(page);
        await Expect(BotonAutorizarOperador(page)).ToBeVisibleAsync();

        await OperarAsync(page, TenantConEncargo);

        // --- Cuentas con rol de Propiedad ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/usuarios");
        Assert.DoesNotContain("/acceso-denegado", page.Url);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ Nuevo usuario", Exact = true }).ClickAsync();
        var drawer = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Nuevo usuario", Exact = true });
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // Barrera positiva: el selector ya ofrece los roles de Operación. Solo entonces cuenta que
        // no ofrezca los de Propiedad.
        var rol = drawer.GetByLabel("Rol", new LocatorGetByLabelOptions { Exact = true });
        await Expect(rol.Locator("option[value='GestorCae']")).ToHaveCountAsync(1);
        await Expect(rol.Locator("option[value='Administrador']")).ToHaveCountAsync(0);
        await Expect(rol.Locator("option[value='DireccionCae']")).ToHaveCountAsync(0);

        // No ofrecerlo es comodidad; la autoridad está en Application. Se fuerza el valor en el
        // navegador, como haría quien manipulara la página, y el alta tiene que fallar.
        var correo = $"administrador.forzado.{Guid.NewGuid().ToString("N")[..8]}@caemanager.local";
        await drawer.GetByLabel("Correo", new LocatorGetByLabelOptions { Exact = true }).FillAsync(correo);
        var nombre = drawer.GetByLabel("Nombre completo", new LocatorGetByLabelOptions { Exact = true });
        await nombre.FillAsync("Administrador forzado (E2E)");
        await nombre.BlurAsync();
        await rol.EvaluateAsync(
            """
            selector => {
                const opcion = document.createElement("option");
                opcion.value = "Administrador";
                opcion.textContent = "Administrador";
                selector.appendChild(opcion);
            }
            """);
        await rol.SelectOptionAsync(new SelectOptionValue { Value = "Administrador" });
        await Expect(rol).ToHaveValueAsync("Administrador");

        await drawer.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Guardar", Exact = true }).ClickAsync();
        await Expect(drawer.Locator(".drawer-aviso")).ToContainTextAsync(
            "Administrador y Dirección CAE solo se asignan desde tu propia organización",
            new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });
        Assert.Equal("0", await fixture.LeerValorSqlAsync(
            """SELECT count(*)::text FROM "AspNetUsers" WHERE "NormalizedEmail" = upper(@correo)""", ("correo", correo)));

        // --- Operadores CAE externos ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/delegaciones");
        await EsperarDelegacionesCargadasAsync(page);
        await Expect(page.GetByTestId("aviso-por-encargo")).ToBeVisibleAsync();
        await Expect(page.Locator(".delegaciones-aviso-ajeno")).ToBeVisibleAsync();
        await Expect(BotonAutorizarOperador(page)).ToHaveCountAsync(0);
    }

    private async Task<IPage> EntrarComoAdministradorDelOperadorAsync(IBrowserContext contexto)
    {
        await fixture.AsegurarEncargoAsync();

        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        return page;
    }

    /// <summary>Cambia al Tenant propietario y comprueba que el selector lo refleja como activo.</summary>
    private async Task OperarAsync(IPage page, string nombreTenant)
    {
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, nombreTenant);
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync(
            "data-tenant-id", await IdDeTenantAsync(nombreTenant));
    }

    /// <summary>
    /// La página decide qué acciones ofrece con las mismas consultas que cargan la lista, y las
    /// asigna antes de retirar el esqueleto de carga: con la lista (o su vacío) a la vista, la
    /// presencia o la ausencia de un botón ya es definitiva.
    /// </summary>
    private static async Task EsperarDelegacionesCargadasAsync(IPage page) =>
        await page.Locator(".delegaciones-lista")
            .Or(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "No hay ninguna delegación", Exact = true }))
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

    private static ILocator BotonAutorizarOperador(IPage page) =>
        page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = AccionAutorizarOperador, Exact = true });

    private Task<string> IdDeTenantAsync(string nombreTenant) =>
        fixture.LeerValorSqlAsync("""SELECT "Id"::text FROM "Tenants" WHERE "Nombre" = @nombre""", ("nombre", nombreTenant));

    private Task<string> LeerUmbralProximoAsync(string nombreTenant) =>
        fixture.LeerValorSqlAsync(
            """
            SELECT parametros."UmbralAmbarDias"::text
            FROM "ParametrosSistema" parametros
            JOIN "Tenants" tenant ON tenant."Id" = parametros."TenantId"
            WHERE tenant."Nombre" = @nombre
            """,
            ("nombre", nombreTenant));

    /// <summary>
    /// El guardado es una ida y vuelta por el circuito: se espera a la fila, con plazo. Agotar el
    /// plazo es un fallo con el último valor leído, no un «dejé de mirar».
    /// </summary>
    private async Task EsperarUmbralProximoAsync(string nombreTenant, string esperado)
    {
        var limite = DateTime.UtcNow.AddSeconds(15);
        var leido = await LeerUmbralProximoAsync(nombreTenant);
        while (leido != esperado && DateTime.UtcNow < limite)
        {
            await Task.Delay(250);
            leido = await LeerUmbralProximoAsync(nombreTenant);
        }

        Assert.Equal(esperado, leido);
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
