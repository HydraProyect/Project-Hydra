using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre el alta de un Cliente Delegante nuevo (crea el tenant, la
/// delegación activa y asigna al creador como Operador Delegado en el mismo
/// paso), operar dentro de su Delegated Workspace, y la revocación
/// (Horizonte 1.6 de Project-Hydra-Negocio/MACRO_PLAN_2026-08-13.md) — complementa a
/// FlujoDelegatedWorkspaceTests (que solo cambia entre workspaces ya
/// existentes) y FlujoSoporteTests (mecanismo de acceso distinto: motivo +
/// ventana, no alta/revocación comercial).
///
/// <para>
/// Revoca y reactiva el Administrador del Operador CAE externo, el mismo que dio de alta
/// el acceso, desde su organización: revocan y reactivan el Gestor CAE, el Coordinador CAE,
/// la Dirección CAE y el Administrador del propio Operador CAE externo con alcance sobre el
/// Tenant propietario, y el Administrador alcanza a toda la organización (decisiones de
/// producto del 2026-10-26).
/// </para>
/// </summary>
[Collection("AppCollection")]
public class FlujoAltaYRevocacionDelegacionTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Alta_de_delegacion_operar_el_workspace_y_revocacion()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var nombreClienteDelegante = $"Delegación E2E {sufijo}";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        // --- Acto fundacional: sin cruzarlo, "Nueva delegación" no existe ---
        // Desde A3 el alta de Cliente Delegante la autoriza AdminPlataforma, y
        // esa capacidad no la siembra ningún seeder: la única forma de obtenerla
        // es que la identidad raíz designada por el despliegue cruce esta puerta.
        // Este test hace lo que haría la identidad raíz en el primer arranque real;
        // insertar la concesión directamente en la base de datos daría el mismo
        // verde sin ejercitar el único camino que la produce.
        //
        // Tolera encontrarla ya cruzada (una segunda ejecución contra la misma
        // base de datos): el acto es de un solo uso y no se reabre. No enmascara
        // nada — si la puerta dejara de producir la capacidad, la espera de
        // "Nueva delegación" seguiría fallando igual unas líneas más abajo.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/configuracion/plataforma");
        var puerta = page.GetByRole(AriaRole.Button,
            new PageGetByRoleOptions { Name = "Inicializar administración global" });
        var yaInicializada = page.GetByRole(AriaRole.Heading,
            new PageGetByRoleOptions { Name = "No hay nada que inicializar aquí" });
        await puerta.Or(yaInicializada).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        if (await puerta.CountAsync() > 0)
        {
            await puerta.ClickAsync();
            var confirmacion = page.GetByRole(AriaRole.Dialog,
                new PageGetByRoleOptions { Name = "Confirmar el acto fundacional" });
            await confirmacion.GetByLabel(
                "Entiendo que este paso se ejecuta una sola vez y no vuelve a estar disponible.").CheckAsync();
            await confirmacion.GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Sí, inicializar" }).ClickAsync();

            // Aserción positiva del resultado, no "el diálogo desapareció":
            // si el comando fallara, la página conserva la puerta y pinta el
            // error, y esto lo delata en vez de dejarlo pasar.
            await Expect(yaInicializada).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        }

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/delegaciones");

        // --- Alta: crea el tenant + delegación activa + operador en un solo paso ---
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Nueva delegación", Exact = true }).ClickAsync();
        var modalNueva = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Nueva delegación", Exact = true });
        await modalNueva.GetByLabel("Nombre de la organización", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombreClienteDelegante);
        // Exact: true -- sin esto, GetByText hace match por substring y
        // también resuelve el párrafo "Se creará una organización..." (que
        // contiene "creará", que empieza igual que "Crear").
        await modalNueva.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Crear", Exact = true }).ClickAsync();
        await modalNueva.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Ayudas.AbrirDetalleDeDelegacionAsync(page, nombreClienteDelegante);
        var tarjeta = page.Locator(".delegaciones-tarjeta", new PageLocatorOptions { Has = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"Gestionamos a {nombreClienteDelegante}", Exact = true }) });
        await tarjeta.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Expect(tarjeta).ToContainTextAsync("Activa");
        // El Administrador del Operador CAE externo alcanza a toda la organización (decisiones del
        // 2026-10-26): revoca sin Asignación de Cartera propia.
        await Expect(tarjeta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Revocar acceso", Exact = true }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // --- Operar el Delegated Workspace recién creado ---
        // SelectorClienteActivo (en el layout) solo carga _clientes en
        // OnInitializedAsync — no hay ningún mecanismo que lo refresque en
        // vivo tras crear una delegación en la misma navegación in-app, así
        // que sin recargar la página la nueva delegación no está todavía en
        // el <select> (ver SelectorClienteActivo.razor.cs). Un reload es
        // además lo que haría un usuario real antes de esperar verla ahí.
        //
        // Esperar NetworkIdle tras el reload (intento anterior) no basta:
        // deja el circuito de SignalR "asentándose" de fondo y esa actividad
        // de reconexión se solapa con el WaitForLoadStateAsync(NetworkIdle)
        // interno de CambiarClienteActivoAsync justo después, adelantando su
        // resolución antes de que la navegación real (el <form> POST que
        // dispara microinteracciones.js) termine — el GotoAsync("/clientes")
        // posterior llegaba entonces con esa navegación todavía en vuelo y el
        // navegador la abortaba (net::ERR_ABORTED). Esperar a que la propia
        // opción de la delegación exista en el <select> es una señal
        // concreta de que OnInitializedAsync ya corrió con el circuito
        // interactivo de verdad, no solo de que la red esté momentáneamente
        // en silencio.
        await page.ReloadAsync();
        // State: Attached, no el Visible por defecto -- un <option> nunca se
        // reporta "visible" para Playwright mientras el <select> está
        // cerrado (no tiene caja de layout real), así que WaitForAsync con
        // el estado por defecto nunca resolvía aunque el elemento ya
        // existiera en el DOM (confirmado en CI: 34 reintentos, siempre
        // resuelto pero "hidden").
        await Ayudas.AbrirSelectorTenantAsync(page);
        await page.Locator(".selector-tenant-panel").GetByRole(AriaRole.Option, new LocatorGetByRoleOptions { Name = nombreClienteDelegante })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 15_000 });
        await Ayudas.CerrarSelectorTenantAsync(page);

        // El tenant nuevo no tiene datos propios todavía (se acaba de crear),
        // así que la comprobación real es que el selector de Cliente activo
        // cambia y la app deja de estar en la Consultora de origen.
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, nombreClienteDelegante);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes");
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync(
            "data-tenant-id", await Ayudas.IdDeOpcionSelectorTenantAsync(page, nombreClienteDelegante) ?? string.Empty);

        // Vuelve al origen: el Administrador revoca y reactiva al final, desde su
        // organización.
        //
        // Misma comprobación concreta que tras el primer cambio (línea
        // arriba), y por el mismo motivo: sin ella, el NavegarYEsperarAsync
        // siguiente puede disparar mientras el <form> POST de este segundo
        // cambio de tenant todavía está resolviéndose, y /delegaciones
        // termina cargando en el workspace delegado equivocado (donde esta
        // tarjeta no existe) — fallo real visto en CI: timeout de 30s
        // esperando la tarjeta tras volver al origen.
        await Ayudas.CambiarClienteActivoAsync(page, fixture.BaseUrl, Ayudas.NombreTenantOrigenPorDefecto);
        await Expect(Ayudas.DisparadorSelectorTenant(page)).ToHaveAttributeAsync(
            "data-tenant-id", await Ayudas.IdDeOpcionSelectorTenantAsync(page, Ayudas.NombreTenantOrigenPorDefecto) ?? string.Empty);

        // --- Revocación: el Administrador del Operador CAE externo, desde su casa ---
        // Revoca, pero no reactiva: eso es solo del Administrador del Tenant propietario (decisión
        // SEG-7 del 2026-10-31). La vista contrasta la autorización real (PuedeRevocarQuery y
        // PuedeReactivarQuery), no solo !OperandoWorkspaceAjeno.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/delegaciones");
        await Ayudas.AbrirDetalleDeDelegacionAsync(page, nombreClienteDelegante);
        var tarjetaTrasVolver = page.Locator(".delegaciones-tarjeta", new PageLocatorOptions { Has = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"Gestionamos a {nombreClienteDelegante}", Exact = true }) });
        var revocar = tarjetaTrasVolver.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Revocar acceso", Exact = true });
        var modalRevocar = page.GetByRole(AriaRole.Dialog, new PageGetByRoleOptions { Name = "Revocar el acceso", Exact = true });
        var confirmarRevocar = modalRevocar.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Revocar acceso", Exact = true });
        await revocar.ClickAsync();
        await modalRevocar.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await confirmarRevocar.ClickAsync();
        await modalRevocar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await Expect(tarjetaTrasVolver).ToContainTextAsync("Revocada");
        var reactivar = tarjetaTrasVolver.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Reactivar", Exact = true });
        await Expect(reactivar).ToBeHiddenAsync();
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
