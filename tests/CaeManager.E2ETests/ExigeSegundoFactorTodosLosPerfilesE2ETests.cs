using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// «Exigir verificación en dos pasos a todos los perfiles» (<c>ParametroSistema.ExigeSegundoFactorTodosLosPerfiles</c>)
/// de punta a punta: el Administrador del Tenant propietario lo conmuta en la pantalla real
/// (<c>/configuracion/params</c>, tarjeta de seguridad de acceso) y las cuentas de ese Tenant, de perfiles que
/// no son Administrador, entran —o dejan de entrar— a la aplicación según el valor. Los tests de Application,
/// Domain e integración prueban cada pieza (el comando, el claim de activación pendiente, el middleware); esto
/// prueba que juntas, con cookie real y circuito real, la exigencia llega a quien la sufre.
///
/// <para>
/// En la colección de <see cref="CuentaAMedioActivarTests"/> porque comparten el estado que dejan: cuentas a
/// medio activar. Usa cuentas que aquel no toca (1 de cada perfil y el Administrador 1) y devuelve el ajuste a
/// «no exigido» en cualquier salida, para no dejarlo encendido a los demás.
/// </para>
/// </summary>
[Collection("AppCollectionCuentaAMedioActivar")]
public class ExigeSegundoFactorTodosLosPerfilesE2ETests(WebAppFixtureCuentaAMedioActivar fixture)
{
    private static readonly string EmailAdministrador = Ayudas.EmailPrueba("administrador", 1);

    /// <summary>Perfiles que no son Administrador (a ese lo obliga su rol, con o sin el ajuste).</summary>
    private static readonly string[] Perfiles = ["gestorcae", "direccioncae", "consulta", "cliente"];

    private static readonly Regex Configurar2fa = new(Regex.Escape("/cuenta/configurar-2fa"));

    [Fact]
    public async Task Con_la_exigencia_activada_por_el_Administrador_cada_perfil_sin_segundo_factor_acaba_en_activarlo()
    {
        await AfirmarMismoTenantQueElAdministradorAsync();
        try
        {
            await ConmutarComoAdministradorAsync(exigir: true);

            foreach (var perfil in Perfiles)
            {
                await using var contexto = await fixture.Browser.NewContextAsync();
                var pagina = await contexto.NewPageAsync();
                await EntrarSinEsperarElMenuAsync(pagina, Ayudas.EmailPrueba(perfil, 1));

                foreach (var ruta in new[] { "/", "/clientes" })
                {
                    await pagina.GotoAsync($"{fixture.BaseUrl}{ruta}");
                    await Expect(pagina).ToHaveURLAsync(Configurar2fa, new PageAssertionsToHaveURLOptions { Timeout = 30_000 });
                }

                // El motivo que se le da es el del Tenant, no el rol de Administrador que no tiene.
                var aviso = pagina.Locator("p.dos-factores-obligatorio");
                await Expect(aviso).ToContainTextAsync("Tu organización exige la autenticación en dos pasos a todas las cuentas");
                await Expect(aviso).Not.ToContainTextAsync("Tu rol de Administrador");
            }
        }
        finally
        {
            await DejarSinExigenciaAsync();
        }
    }

    [Fact]
    public async Task Al_desactivar_la_exigencia_la_misma_cuenta_que_estaba_bloqueada_entra_con_su_contrasena()
    {
        await AfirmarMismoTenantQueElAdministradorAsync();
        try
        {
            await FijarExigenciaAsync(exigir: true);

            // Control positivo: con la exigencia puesta, esta cuenta no pasa de la pantalla de activación.
            await using (var bloqueada = await fixture.Browser.NewContextAsync())
            {
                var paginaBloqueada = await bloqueada.NewPageAsync();
                await EntrarSinEsperarElMenuAsync(paginaBloqueada, Ayudas.EmailPrueba("gestorcae", 1));
                await paginaBloqueada.GotoAsync($"{fixture.BaseUrl}/");
                await Expect(paginaBloqueada).ToHaveURLAsync(Configurar2fa, new PageAssertionsToHaveURLOptions { Timeout = 30_000 });
            }

            await ConmutarComoAdministradorAsync(exigir: false);

            foreach (var perfil in Perfiles)
            {
                await using var contexto = await fixture.Browser.NewContextAsync();
                var pagina = await contexto.NewPageAsync();

                // IniciarSesionAsync espera al menú de la aplicación: solo llega a él quien no tiene nada pendiente.
                await Ayudas.IniciarSesionAsync(pagina, fixture.BaseUrl, Ayudas.EmailPrueba(perfil, 1), Ayudas.ContrasenaUsuariosPrueba);
                await pagina.GotoAsync($"{fixture.BaseUrl}/");
                await Expect(pagina.Locator(".nav-principal")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
                Assert.DoesNotContain("/cuenta/", pagina.Url);
            }
        }
        finally
        {
            await DejarSinExigenciaAsync();
        }
    }

    /// <summary>
    /// Conmuta el interruptor de la tarjeta de seguridad de acceso como el Administrador del Tenant y guarda;
    /// espera al aviso de la pantalla y comprueba el valor en la base (el aviso solo sale tras guardar).
    /// </summary>
    private async Task ConmutarComoAdministradorAsync(bool exigir)
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var pagina = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(pagina, fixture.BaseUrl, EmailAdministrador, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(pagina, $"{fixture.BaseUrl}/configuracion/params");

        var tarjeta = pagina.GetByTestId("seguridad-acceso");
        await Expect(tarjeta).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        var interruptor = tarjeta.GetByRole(AriaRole.Switch,
            new LocatorGetByRoleOptions { Name = "Exigir verificación en dos pasos a todos los perfiles" });
        var esperado = exigir ? "true" : "false";

        // Un clic antes de que el circuito conecte no hace nada (el HTML prerenderizado ya está, su @onclick no):
        // se repite hasta que el interruptor cambia. Solo se pulsa si no está como se quiere.
        for (var intento = 1; await interruptor.GetAttributeAsync("aria-checked") != esperado; intento++)
        {
            await interruptor.ClickAsync();
            try
            {
                await Expect(interruptor).ToHaveAttributeAsync("aria-checked", esperado, new LocatorAssertionsToHaveAttributeOptions { Timeout = 3_000 });
            }
            catch (PlaywrightException) when (intento < 5)
            {
            }
        }

        // Guardar es idempotente: repetirlo mientras no salga el aviso solo cuesta otra escritura del mismo valor.
        var guardar = tarjeta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Guardar", Exact = true });
        for (var intento = 1; ; intento++)
        {
            await guardar.ClickAsync();
            try
            {
                await Expect(pagina.Locator(".toast")).ToContainTextAsync(
                    "Seguridad de acceso actualizada.", new LocatorAssertionsToContainTextOptions { Timeout = 8_000 });
                break;
            }
            catch (PlaywrightException) when (intento < 3)
            {
            }
        }

        Assert.Equal(exigir ? "true" : "false", await LeerExigenciaAsync());
    }

    private Task FijarExigenciaAsync(bool exigir) =>
        EjecutarSobreElTenantDelAdministradorAsync(exigir ? "true" : "false");

    private Task DejarSinExigenciaAsync() => FijarExigenciaAsync(exigir: false);

    private async Task EjecutarSobreElTenantDelAdministradorAsync(string valor) =>
        Assert.Equal(1, await fixture.EjecutarSqlAsync(
            """
            UPDATE "ParametrosSistema" SET "ExigeSegundoFactorTodosLosPerfiles" = @v::boolean
            WHERE "TenantId" = (SELECT "TenantId" FROM "AspNetUsers" WHERE "Email" = @e)
            """,
            ("v", valor), ("e", EmailAdministrador)));

    private Task<string> LeerExigenciaAsync() =>
        fixture.LeerValorSqlAsync(
            """
            SELECT parametros."ExigeSegundoFactorTodosLosPerfiles"::text
            FROM "ParametrosSistema" parametros
            WHERE parametros."TenantId" = (SELECT "TenantId" FROM "AspNetUsers" WHERE "Email" = @e)
            """,
            ("e", EmailAdministrador));

    /// <summary>
    /// El ajuste es del Tenant propietario de la cuenta: sin este control, «todos los perfiles» podría valer para
    /// un Tenant distinto del que sembró esas cuentas y el test seguiría en verde por otra razón.
    /// </summary>
    private async Task AfirmarMismoTenantQueElAdministradorAsync()
    {
        var tenantAdministrador = await fixture.LeerValorSqlAsync(
            """SELECT "TenantId"::text FROM "AspNetUsers" WHERE "Email" = @e""", ("e", EmailAdministrador));
        foreach (var perfil in Perfiles)
            Assert.Equal(tenantAdministrador, await fixture.LeerValorSqlAsync(
                """SELECT "TenantId"::text FROM "AspNetUsers" WHERE "Email" = @e""", ("e", Ayudas.EmailPrueba(perfil, 1))));
    }

    private async Task EntrarSinEsperarElMenuAsync(IPage pagina, string email)
    {
        await pagina.GotoAsync($"{fixture.BaseUrl}/cuenta/iniciar-sesion");
        await pagina.FillAsync("#email", email);
        await pagina.FillAsync("#password", Ayudas.ContrasenaUsuariosPrueba);
        await pagina.ClickAsync("button[type=\"submit\"]");
        await pagina.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }
}
