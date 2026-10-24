using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// El Administrador del Operador CAE externo de demo en los Tenants beneficiarios que opera, con un
/// navegador real. Regla de Chris del 2026-10-20 (opción A, PR #1488): el usuario del Operador CAE
/// externo tiene su rol de origen, con todos sus poderes y también los de Propiedad, en cada Tenant
/// beneficiario que cubre una Asignación de Operación vigente, externa y del Tenant entero de su
/// organización, haya o no Encargo de administración. Deroga en ese punto la decisión del
/// 2026-10-15 («sin encargo no administra») y el techo por encargo de la decisión D-8 (2026-10-08).
///
/// <para>
/// La fixture sigue sembrando el encargo de <see cref="WebAppFixtureEncargoAdministracion.TenantConEncargo"/>
/// y deja <see cref="WebAppFixtureEncargoAdministracion.TenantSinEncargo"/> sin él: los dos se
/// administran igual, y eso es lo que se mide. También le siembra una Asignación de Cartera de
/// Gestor CAE en los dos, que ya no decide su rol en ellos.
/// </para>
///
/// <list type="number">
/// <item>Con encargo y sin él cambia la configuración del Tenant beneficiario, y solo la de ese.</item>
/// <item>Sin encargo da de alta una cuenta con rol Administrador del Tenant beneficiario.</item>
/// <item>Sin Asignación de Operación sobre un Tenant no entra en él: la frontera de Tenant sigue
/// cerrada.</item>
/// </list>
/// </summary>
[Collection("AppCollectionEncargoAdministracion")]
public class EncargoAdministracionE2ETests(WebAppFixtureEncargoAdministracion fixture)
{
    private const string TenantConEncargo = WebAppFixtureEncargoAdministracion.TenantConEncargo;
    private const string TenantSinEncargo = WebAppFixtureEncargoAdministracion.TenantSinEncargo;

    /// <summary>Tenant que la organización del Operador CAE externo de demo no opera.</summary>
    private const string TenantNoOperado = Ayudas.NombreTenantOrigenPorDefecto;

    private const string RotuloUmbralProximo = "Umbral próximo (días)";

    [Theory]
    [InlineData(TenantSinEncargo, TenantConEncargo)]
    [InlineData(TenantConEncargo, TenantSinEncargo)]
    public async Task El_Administrador_del_Operador_CAE_externo_cambia_la_configuracion_del_Tenant_beneficiario_con_o_sin_encargo(
        string tenant, string otroTenant)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await EntrarComoAdministradorDelOperadorAsync(contexto);

        var umbralAntes = int.Parse(await LeerUmbralProximoAsync(tenant));
        var umbralOtroAntes = await LeerUmbralProximoAsync(otroTenant);
        var umbralOperadorAntes = await LeerUmbralProximoAsync(Ayudas.NombreTenantConsultora);
        var umbralNuevo = umbralAntes + 7;

        await OperarAsync(page, tenant);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/configuracion/params");
        Assert.DoesNotContain("/acceso-denegado", page.Url);

        // El panel cargó con el valor del Tenant beneficiario, no con el de su Tenant de origen.
        var campo = page.GetByLabel(RotuloUmbralProximo, new PageGetByLabelOptions { Exact = true });
        await Expect(campo).ToHaveValueAsync(
            umbralAntes.ToString(), new LocatorAssertionsToHaveValueOptions { Timeout = 15_000 });

        // Con el panel ya cargado: las exclusiones de los encargos no plenos ya no rigen, y el
        // presupuesto de IA se ofrece en vez de salir como reservado.
        await Expect(page.GetByLabel("Presupuesto mensual (USD)", new PageGetByLabelOptions { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByTestId("presupuesto-ia-reservado")).ToHaveCountAsync(0);

        await campo.FillAsync(umbralNuevo.ToString());
        await campo.BlurAsync();
        await page.Locator(".tarjeta-configuracion").Filter(new LocatorFilterOptions { Has = campo })
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Guardar cambios", Exact = true })
            .ClickAsync();

        // El cambio llegó a la base, en la fila del Tenant beneficiario.
        await EsperarUmbralProximoAsync(tenant, umbralNuevo.ToString());

        // Y solo en esa: ni el otro Tenant beneficiario ni el Tenant de origen del Operador CAE.
        Assert.Equal(umbralOtroAntes, await LeerUmbralProximoAsync(otroTenant));
        Assert.Equal(umbralOperadorAntes, await LeerUmbralProximoAsync(Ayudas.NombreTenantConsultora));

        // Tras recargar, la pantalla lo lee de vuelta.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/configuracion/params");
        await Expect(page.GetByLabel(RotuloUmbralProximo, new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync(
            umbralNuevo.ToString(), new LocatorAssertionsToHaveValueOptions { Timeout = 15_000 });
    }

    [Fact]
    public async Task Sin_encargo_el_Administrador_del_Operador_CAE_externo_da_de_alta_un_Administrador_del_Tenant_propietario_que_opera()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await EntrarComoAdministradorDelOperadorAsync(contexto);

        await OperarAsync(page, TenantSinEncargo);

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/usuarios");
        Assert.DoesNotContain("/acceso-denegado", page.Url);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ Nuevo usuario", Exact = true }).ClickAsync();
        var drawer = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Nuevo usuario", Exact = true });
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // El selector ofrece los roles de Propiedad del Tenant beneficiario.
        var rol = drawer.GetByLabel("Rol", new LocatorGetByLabelOptions { Exact = true });
        await Expect(rol.Locator("option[value='Administrador']")).ToHaveCountAsync(1);
        await Expect(rol.Locator("option[value='DireccionCae']")).ToHaveCountAsync(1);

        var correo = $"administrador.operador.{Guid.NewGuid().ToString("N")[..8]}@caemanager.local";
        await drawer.GetByLabel("Correo", new LocatorGetByLabelOptions { Exact = true }).FillAsync(correo);
        var nombre = drawer.GetByLabel("Nombre completo", new LocatorGetByLabelOptions { Exact = true });
        await nombre.FillAsync("Administrador dado de alta por el Operador CAE externo (E2E)");
        await nombre.BlurAsync();
        await rol.SelectOptionAsync(new SelectOptionValue { Value = "Administrador" });
        await Expect(rol).ToHaveValueAsync("Administrador");

        // Este «Guardar» es el que falló 16 veces en 7 días de CI sin que se supiera por qué: si vuelve a
        // pasar, el mensaje trae el estado del DOM (ver PulsarGuardarDelDrawerAsync).
        await Ayudas.PulsarGuardarDelDrawerAsync(
            page, drawer.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Crear y enviar enlace", Exact = true }));

        // La cuenta existe en el Tenant beneficiario, no en el de origen del Operador CAE externo, y
        // con el rol Administrador.
        var tenantId = await IdDeTenantAsync(TenantSinEncargo);
        await EsperarValorSqlAsync(
            "1",
            """
            SELECT count(*)::text
            FROM "AspNetUsers" u
            JOIN "AspNetUserRoles" ur ON ur."UserId" = u."Id"
            JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
            WHERE u."NormalizedEmail" = upper(@correo)
              AND u."TenantId"::text = @tenant
              AND r."Name" = 'Administrador'
            """,
            ("correo", correo), ("tenant", tenantId));
    }

    /// <summary>
    /// La regla del 2026-10-20 abre los poderes del rol de origen, no la frontera de Tenant: sin una
    /// Asignación de Operación (ni la vía heredada) sobre un Tenant, el Administrador del Operador
    /// CAE externo no entra. Las precondiciones se comprueban en la base para que el «no entra» no
    /// sea vacío, y el selector tiene que ofrecer antes los Tenants que sí opera.
    /// </summary>
    [Fact]
    public async Task Sin_Asignacion_de_Operacion_el_Administrador_del_Operador_CAE_externo_no_entra_en_el_Tenant()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await EntrarComoAdministradorDelOperadorAsync(contexto);

        var tenantNoOperado = await IdDeTenantAsync(TenantNoOperado);
        Assert.False(string.IsNullOrEmpty(tenantNoOperado), $"La siembra no tiene el Tenant «{TenantNoOperado}».");
        Assert.Equal("0", await fixture.LeerValorSqlAsync(
            """
            SELECT count(*)::text
            FROM "AsignacionesOperacion" o
            JOIN "Tenants" operador ON operador."Id" = o."OperadorTenantId"
            WHERE operador."Nombre" = @operador AND o."PropietarioTenantId"::text = @tenant
            """,
            ("operador", Ayudas.NombreTenantConsultora), ("tenant", tenantNoOperado)));
        Assert.Equal("0", await fixture.LeerValorSqlAsync(
            """
            SELECT count(*)::text
            FROM "AsignacionesOperadorDelegado" f
            JOIN "DelegacionesTenant" d ON d."Id" = f."DelegacionTenantId"
            JOIN "AspNetUsers" u ON u."Id" = f."UsuarioId"
            WHERE u."NormalizedEmail" = upper(@correo) AND d."TenantClienteId"::text = @tenant
            """,
            ("correo", Ayudas.EmailAdministradorConsultora), ("tenant", tenantNoOperado)));

        // El selector ofrece los Tenants que opera y no el otro.
        await Ayudas.AbrirSelectorTenantAsync(page);
        var panel = page.Locator(".selector-tenant-panel");
        await Expect(panel.GetByRole(AriaRole.Option, new LocatorGetByRoleOptions { Name = TenantSinEncargo })).ToHaveCountAsync(1);
        await Expect(panel.GetByRole(AriaRole.Option, new LocatorGetByRoleOptions { Name = TenantNoOperado })).ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("Escape");

        // Forzar la selección, como haría quien manipulara la página: el servidor la deniega.
        var token = await Ayudas.TokenAntiforgeryAsync(page);
        Assert.False(string.IsNullOrEmpty(token), "La página no tiene token antiforgery.");
        var respuesta = await page.RunAndWaitForResponseAsync(
            () => page.EvaluateAsync(
                """
                ([tenant, token]) => {
                    const form = document.createElement("form");
                    form.method = "post";
                    form.action = "/cuenta/cliente-activo";
                    for (const [nombre, valor] of [["__RequestVerificationToken", token], ["tenantId", tenant], ["returnUrl", "/"]]) {
                        const campo = document.createElement("input");
                        campo.type = "hidden";
                        campo.name = nombre;
                        campo.value = valor;
                        form.appendChild(campo);
                    }
                    document.body.appendChild(form);
                    form.submit();
                }
                """, new[] { tenantNoOperado, token! }),
            r => r.Url.Contains("/cuenta/cliente-activo") && r.Request.Method == "POST");

        Assert.True(respuesta.Status is >= 300 and < 400,
            $"POST a /cuenta/cliente-activo devolvió {respuesta.Status}: se esperaba la redirección de Forbid.");
        Assert.Contains("acceso-denegado", respuesta.Headers.GetValueOrDefault("location") ?? string.Empty);

        // Se espera a que el navegador aterrice en la redirección antes de navegar otra vez: sin
        // esto, el Goto siguiente interrumpe esa navegación (mismo caso que GestorCaeCarteraMultiTenantTests).
        await page.WaitForURLAsync("**/acceso-denegado**", new PageWaitForURLOptions { Timeout = 15_000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Y la sesión no quedó en ese Tenant: el selector sigue mostrando uno de los que opera.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/mi-trabajo");
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync(
            "data-tenant-id", new System.Text.RegularExpressions.Regex(".+"), new() { Timeout = 15_000 });
        await Expect(Ayudas.DisparadorSelectorTenant(page)).Not.ToHaveAttributeAsync(
            "data-tenant-id", tenantNoOperado, new() { Timeout = 15_000 });
    }

    private async Task<IPage> EntrarComoAdministradorDelOperadorAsync(IBrowserContext contexto)
    {
        await fixture.AsegurarEncargoAsync();

        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(
            page, fixture.BaseUrl, Ayudas.EmailAdministradorConsultora, Ayudas.ContrasenaUsuariosPrueba);
        return page;
    }

    /// <summary>Cambia al Tenant beneficiario y comprueba que el selector lo refleja como activo.</summary>
    private async Task OperarAsync(IPage page, string nombreTenant)
    {
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, nombreTenant);
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync(
            "data-tenant-id", await IdDeTenantAsync(nombreTenant));
    }

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

    private Task EsperarUmbralProximoAsync(string nombreTenant, string esperado) =>
        EsperarValorSqlAsync(
            esperado,
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
    private async Task EsperarValorSqlAsync(string esperado, string sql, params (string Nombre, string Valor)[] parametros)
    {
        var limite = DateTime.UtcNow.AddSeconds(15);
        var leido = await fixture.LeerValorSqlAsync(sql, parametros);
        while (leido != esperado && DateTime.UtcNow < limite)
        {
            await Task.Delay(250);
            leido = await fixture.LeerValorSqlAsync(sql, parametros);
        }

        Assert.Equal(esperado, leido);
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
