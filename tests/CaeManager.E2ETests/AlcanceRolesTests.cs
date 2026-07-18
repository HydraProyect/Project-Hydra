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
    [GeneratedRegex(@"\d+ items")]
    private static partial Regex PatronContadorElementos();

    [GeneratedRegex(@"(\d+)\s+items")]
    private static partial Regex PatronTotalElementos();

    /// <summary>
    /// El Paginator de QuickGrid renderiza algo como "1–20 of 200 items" —
    /// el número justo antes de "items" es siempre TotalItemCount (ver la
    /// plantilla compilada del paquete), a diferencia del primer número del
    /// rango, que cambia según la página. Se extrae ese para comparar el
    /// total real en vez de asumir un formato de texto completo fijo.
    /// </summary>
    private static int ExtraerTotalElementos(string textoPaginador)
    {
        var coincidencia = PatronTotalElementos().Match(textoPaginador);
        if (!coincidencia.Success)
            throw new InvalidOperationException($"No se encontró un total de elementos en «{textoPaginador}».");

        return int.Parse(coincidencia.Groups[1].Value);
    }

    [Fact]
    public async Task Administrador_ve_los_200_clientes_sembrados()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        var contador = page.GetByText(PatronContadorElementos()).First;
        await contador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // >=200, no ==200: la app se comparte con el resto de tests de esta
        // colección (un solo arranque, ver WebAppFixture) y FlujoCriticoTests
        // puede haber creado algún Cliente adicional antes de que este test
        // corra — lo que importa aquí es "sin restricción" (el total base +
        // lo que sea que ya exista), no un número exacto congelado.
        Assert.True(ExtraerTotalElementos(await contador.InnerTextAsync()) >= 200);
    }

    /// <summary>
    /// GestorCae solo ve su propia cartera (Cliente.EjecutivoUsuarioId) — el
    /// primer usuario de prueba de este rol tiene exactamente 10 de los 200
    /// clientes sembrados (ver DatosPruebaSeeder, "cartera de prueba": los
    /// primeros 30 clientes repartidos entre 3 gestores round-robin). Se
    /// comprueba el reparto exacto porque la semilla aleatoria es fija
    /// (Random(20260716)) — no es un número arbitrario.
    /// </summary>
    [Fact]
    public async Task GestorCae_ve_solo_su_cartera_acotada()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("gestorcae", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        var contador = page.GetByText(PatronContadorElementos()).First;
        await contador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        var total = ExtraerTotalElementos(await contador.InnerTextAsync());
        Assert.Equal(10, total);
    }

    [Fact]
    public async Task Consulta_ve_todo_pero_no_puede_crear_un_cliente()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailPrueba("consulta", 1), Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");

        var contador = page.GetByText(PatronContadorElementos()).First;
        await contador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        Assert.True(ExtraerTotalElementos(await contador.InnerTextAsync()) >= 200);

        await page.GetByText("+ Nuevo cliente").ClickAsync();
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
        await nav.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
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
