using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Instancia propia bajo <c>cae_app_runtime</c>: la concesión de SoporteLectura que
/// siembran estos tests, y la Sesión Privilegiada que abren, no deben aparecer en
/// ninguna pantalla de <see cref="AccesoBajoRolRuntimeTests"/>.
/// </summary>
public sealed class WebAppFixtureSoporteBajoRuntime : WebAppFixtureBajoRuntime;

[CollectionDefinition("AppCollectionSoporteBajoRuntime")]
public class AppCollectionSoporteBajoRuntime : ICollectionFixture<WebAppFixtureSoporteBajoRuntime>;

/// <summary>
/// Actor de Plataforma TALVEG con Sesión Privilegiada y sin Usuario simulado, en los
/// listados de <c>/centros</c>, <c>/trabajadores</c> y <c>/empresas</c>, con la conexión
/// autenticada como <c>cae_app_runtime</c> y el <c>SET ROLE cae_app_soporte</c> real del
/// interceptor. Ningún otro E2E abre esas rutas con esa identidad efectiva: el resto de
/// fixtures conecta como propietario, a quien PostgreSQL no somete a RLS ni a GRANT.
///
/// <para>
/// La sesión se abre por <c>/cuenta/soporte/abrir</c>, el único emisor de la cookie. La
/// concesión se siembra por SQL porque la pantalla de plataforma solo ofrece la
/// auto-concesión fundacional de AdminPlataforma, y el POST se hace a mano porque ninguna
/// pantalla publica hoy ese formulario.
/// </para>
/// </summary>
[Collection("AppCollectionSoporteBajoRuntime")]
public class SoporteTalvegEnListadosBajoRuntimeTests(WebAppFixtureSoporteBajoRuntime fixture)
{
    private const int CargasPorRuta = 5;

    private static readonly string[] Rutas = ["/centros", "/trabajadores", "/empresas"];

    [Theory]
    [InlineData("/centros")]
    [InlineData("/trabajadores")]
    [InlineData("/empresas")]
    public async Task Una_Sesion_Privilegiada_sin_Usuario_simulado_abre_el_listado_bajo_cae_app_soporte(string ruta)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();
        await AbrirSesionPrivilegiadaAsync(contexto, pagina);

        // Varias cargas frías: la concurrencia sobre el DbContext en el prerender no falla en todas.
        var fallos = RegistrarRespuestasFallidas(pagina);
        for (var carga = 0; carga < CargasPorRuta; carga++)
        {
            var respuesta = await pagina.GotoAsync($"{fixture.BaseUrl}{ruta}");

            Assert.True(respuesta!.Status == 200, $"{ruta}, carga {carga}: respondió {respuesta.Status}.");
            await EsperarListadoConFilasAsync(pagina);
        }

        Assert.Empty(fallos);
    }

    /// <summary>
    /// La misma lectura sin Sesión Privilegiada no adopta el rol, y por eso es la frontera
    /// del diagnóstico: el Tenant propio del Actor de Plataforma TALVEG, con la misma
    /// identidad de conexión, no debe responder 500 aunque no tenga datos de negocio.
    /// </summary>
    [Fact]
    public async Task El_Actor_de_Plataforma_en_su_propio_Tenant_abre_los_tres_listados()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(pagina, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        var fallos = RegistrarRespuestasFallidas(pagina);
        foreach (var ruta in Rutas)
        {
            var respuesta = await pagina.GotoAsync($"{fixture.BaseUrl}{ruta}");
            Assert.True(respuesta!.Status == 200, $"{ruta} respondió {respuesta.Status}.");
        }

        Assert.Empty(fallos);
    }

    /// <summary>
    /// La otra lectura posible del «Soporte» del hallazgo: una ventana de Soporte
    /// (<c>DelegacionTenant</c>, sin Sesión Privilegiada ni <c>SET ROLE</c>), que es lo único
    /// que hoy puede abrirse desde una pantalla, con el rol resuelto contra la base.
    /// </summary>
    [Fact]
    public async Task Una_ventana_de_Soporte_abre_los_tres_listados_bajo_cae_app_runtime()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(pagina, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(pagina, $"{fixture.BaseUrl}/delegaciones");

        await Ayudas.AbrirDetalleDeDelegacionAsync(pagina, Ayudas.NombreClienteDelegadoDemo, soporte: true);
        var tarjeta = pagina.Locator(".delegaciones-tarjeta")
            .Filter(new LocatorFilterOptions { Has = pagina.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = Ayudas.NombreClienteDelegadoDemo }) })
            .Filter(new LocatorFilterOptions { Has = pagina.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Ver actividad registrada", Exact = true }) });
        await tarjeta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Abrir acceso", Exact = true }).ClickAsync();
        var modal = pagina.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Abrir acceso de soporte", Exact = true });
        await modal.GetByLabel("Motivo", new LocatorGetByLabelOptions { Exact = true }).FillAsync("E2E: ventana de soporte bajo runtime");
        await modal.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Abrir acceso", Exact = true }).ClickAsync();
        await tarjeta.GetByText("Acceso abierto", new LocatorGetByTextOptions { Exact = true })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        await Ayudas.NavegarYEsperarAsync(pagina, $"{fixture.BaseUrl}/");
        await Ayudas.CambiarClienteActivoAsync(pagina, fixture.BaseUrl, Ayudas.NombreClienteDelegadoDemo);

        var fallos = RegistrarRespuestasFallidas(pagina);
        foreach (var ruta in Rutas)
        {
            var respuesta = await pagina.GotoAsync($"{fixture.BaseUrl}{ruta}");
            Assert.True(respuesta!.Status == 200, $"{ruta} respondió {respuesta.Status}.");
            await pagina.Locator(".aviso-sesion-soporte").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await Assertions.Expect(pagina.Locator("[data-limite-errores]")).ToHaveCountAsync(0);
        }

        Assert.Empty(fallos);
    }

    /// <summary>
    /// Control positivo del instrumento: si la conexión no adoptara <c>cae_app_soporte</c>,
    /// retirarle el SELECT no cambiaría nada y los verdes de arriba no probarían el rol. Fija
    /// además lo que ve quien sufre un 42501 en la carga del listado: la raíz lo contiene y la
    /// respuesta sigue siendo 200 con un aviso, no un 500. El GRANT es de esta base, propia de
    /// la fixture, y se repone aunque el test falle.
    /// </summary>
    [Fact]
    public async Task Sin_SELECT_para_cae_app_soporte_el_listado_avisa_del_fallo_sin_responder_500()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();
        await AbrirSesionPrivilegiadaAsync(contexto, pagina);

        await fixture.EjecutarSqlAsync("""REVOKE SELECT ON "Centros" FROM cae_app_soporte""");
        try
        {
            var respuesta = await pagina.GotoAsync($"{fixture.BaseUrl}/centros");

            Assert.Equal(200, respuesta!.Status);
            await Assertions.Expect(pagina.GetByText("No pudimos cargar los centros"))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            await Assertions.Expect(pagina.Locator(".enlace-nombre-fila")).ToHaveCountAsync(0);
        }
        finally
        {
            await fixture.EjecutarSqlAsync("""GRANT SELECT ON "Centros" TO cae_app_soporte""");
        }
    }

    private async Task AbrirSesionPrivilegiadaAsync(IBrowserContext contexto, IPage pagina)
    {
        await Ayudas.IniciarSesionAsync(pagina, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        var usuarioId = await fixture.LeerValorSqlAsync(
            """SELECT "Id"::text FROM "AspNetUsers" WHERE "NormalizedEmail" = upper(@email)""",
            ("email", Ayudas.EmailAdministrador));
        var tenantObjetivo = await fixture.LeerValorSqlAsync(
            """
            SELECT c."TenantId"::text FROM "Centros" c
            JOIN "Tenants" t ON t."Id" = c."TenantId"
            WHERE NOT t."EsPlataforma"
            GROUP BY c."TenantId" ORDER BY count(*) DESC LIMIT 1
            """);
        var concesion = await fixture.LeerValorSqlAsync(
            """
            WITH existente AS (
                SELECT "Id" FROM "ConcesionesPrivilegio"
                WHERE "UsuarioPlataformaId" = @usuario::uuid AND "Capacidad" = 'SoporteLectura'
            ), nueva AS (
                INSERT INTO "ConcesionesPrivilegio" (
                    "Id", "UsuarioPlataformaId", "Capacidad", "EsAlcanceGlobal", "VigenciaDesde", "VigenciaHasta",
                    "Estado", "ConcedidaPorUsuarioId", "MotivoConcesion", "Origen", "Version", "CreadoEnUtc")
                SELECT gen_random_uuid(), @usuario::uuid, 'SoporteLectura', true, now() - interval '1 minute',
                       now() + interval '1 day', 'Vigente', @usuario::uuid, 'E2E: Soporte TALVEG bajo runtime',
                       'Ordinaria', gen_random_uuid(), now()
                WHERE NOT EXISTS (SELECT 1 FROM existente)
                RETURNING "Id"
            )
            SELECT "Id"::text FROM existente UNION ALL SELECT "Id"::text FROM nueva
            """,
            ("usuario", usuarioId));

        var token = await pagina.Locator("input[name=__RequestVerificationToken]").First.GetAttributeAsync("value");
        var respuesta = await contexto.APIRequest.PostAsync($"{fixture.BaseUrl}/cuenta/soporte/abrir", new APIRequestContextOptions
        {
            MaxRedirects = 0,
            Form = contexto.APIRequest.CreateFormData()
                .Set("__RequestVerificationToken", token!)
                .Set("concesionPrivilegioId", concesion)
                .Set("tenantObjetivoId", tenantObjetivo)
                .Set("motivo", "E2E: Soporte TALVEG bajo runtime")
                .Set("horasDeVentana", "1"),
        });

        // Un comando fallido también redirige, a "/?errorSoporte=…": solo "/" es una sesión abierta.
        Assert.Equal(302, respuesta.Status);
        Assert.Equal("/", respuesta.Headers["location"]);

        await pagina.GotoAsync(fixture.BaseUrl);
        await pagina.Locator(".aviso-sesion-soporte").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
    }

    private static async Task EsperarListadoConFilasAsync(IPage pagina)
    {
        await pagina.Locator(".aviso-sesion-soporte").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await pagina.Locator(".enlace-nombre-fila").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    /// <summary>También las peticiones secundarias: un 500 del circuito o de un endpoint no aparece en la navegación.</summary>
    private static List<string> RegistrarRespuestasFallidas(IPage pagina)
    {
        var fallos = new List<string>();
        pagina.Response += (_, respuesta) =>
        {
            if (respuesta.Status >= 500)
                fallos.Add($"{respuesta.Status} {respuesta.Url}");
        };
        return fallos;
    }
}
