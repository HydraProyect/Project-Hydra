using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre el asistente de alta encadenada Cliente → Empresa → Centro
/// (Horizonte 1.6 de MACRO_PLAN_2026-08-13.md, flujo de demo "alta guiada
/// completa") — sin ningún E2E hasta ahora, a diferencia del alta manual
/// paso a paso que ya cubre FlujoCriticoTests. Verifica en particular el
/// guardado incremental real (cada paso persiste antes de continuar, no es
/// una transacción larga) siguiendo el propio Cliente/Empresa creados a
/// través de los tres pasos.
/// </summary>
[Collection("AppCollection")]
public class AltaGuiadaTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Asistente_de_alta_guiada_crea_Cliente_Empresa_y_Centro_encadenados()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocialCliente = $"AltaGuiada Cliente {sufijo}";
        var razonSocialEmpresa = $"AltaGuiada Empresa {sufijo}";
        var nombreCentro = $"AltaGuiada Centro {sufijo}";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/alta-guiada");

        // --- Paso 1: Cliente ---
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "1. Cliente" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await page.GetByLabel("CIF", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_997_701));
        await page.GetByText("Guardar y continuar a Empresa").ClickAsync();

        // --- Paso 2: Empresa (nueva, no vincular una existente) ---
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "2. Empresa" }).WaitForAsync();
        // El resumen del paso demuestra que el Cliente del paso 1 ya está
        // persistido y encadenado — no es solo estado en memoria del wizard.
        await Expect(page.Locator(".texto-vacio-seccion")).ToContainTextAsync(razonSocialCliente);

        await page.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        await page.GetByLabel("CIF", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_997_702));
        await page.GetByText("Guardar y continuar a Centro").ClickAsync();

        // --- Paso 3: Centro ---
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "3. Centro" }).WaitForAsync();
        await Expect(page.Locator(".texto-vacio-seccion")).ToContainTextAsync(razonSocialCliente);
        await Expect(page.Locator(".texto-vacio-seccion")).ToContainTextAsync(razonSocialEmpresa);

        // Antes de guardar el primer Centro, "Terminar aquí" está visible
        // (_centrosCreados == 0) — confirma el estado de partida del paso.
        await Expect(page.GetByText("Terminar aquí")).ToBeVisibleAsync();

        await page.GetByLabel("Nombre", new PageGetByLabelOptions { Exact = true }).FillAsync(nombreCentro);
        await page.GetByText("Guardar centro").ClickAsync();

        // "Terminar aquí" desaparece (_centrosCreados pasa a 1) — es la señal
        // de que el Centro se guardó de verdad, sin depender de leer un
        // toast. (El comentario del propio código habla de limpiar el
        // formulario tras guardar, pero en la práctica el campo "Nombre"
        // sigue mostrando el valor guardado — no es lo que este test
        // necesita verificar, así que no se afirma aquí.)
        await Expect(page.GetByText("Terminar aquí")).Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Los tres pasos quedan marcados como completados en el stepper.
        var pasosCompletados = page.Locator(".indicador-pasos-item.indicador-pasos-completado");
        await Expect(pasosCompletados).ToHaveCountAsync(3);

        // --- Verificación final: los tres registros existen de verdad, consultando cada pantalla ---
        // F3b (2026-08-26): el Cliente ya no se verifica en /clientes —
        // ObtenerClientesQuery es una de las 6 consultas que D2 deja leyendo
        // la tabla legacy Clientes hasta F4, y con los escritores
        // redirigidos a Empresa esa pantalla queda vacía en cualquier
        // entorno (decisión explícita: "aceptar el vacío", ver
        // f3b-decision-d2-transicion-acotada-2026-08-25.md). El Cliente
        // recién creado sí existe como fila en /empresas (EsCritico != null,
        // sin consulta congelada), así que la verificación se reancla ahí.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/empresas");
        await page.GetByPlaceholder("Buscar por razón social…").FillAsync(razonSocialCliente);
        await page.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = razonSocialCliente })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        await page.GetByPlaceholder("Buscar por razón social…").FillAsync(razonSocialEmpresa);
        await page.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = razonSocialEmpresa })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        await page.GetByPlaceholder("Buscar centro, cliente o empresa…").FillAsync(nombreCentro);
        await page.GetByText(nombreCentro).WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
