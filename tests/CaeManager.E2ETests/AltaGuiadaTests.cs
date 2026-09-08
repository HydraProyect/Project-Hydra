using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre el asistente de alta encadenada Empresa → Cliente → Centro →
/// Trabajadores (Horizonte 1.6 de MACRO_PLAN_2026-08-13.md, flujo de demo
/// "alta guiada completa") — sin ningún E2E hasta ahora, a diferencia del
/// alta manual paso a paso que ya cubre FlujoCriticoTests. Verifica en
/// particular el guardado incremental real (cada paso persiste antes de
/// continuar, no es una transacción larga) siguiendo la propia
/// Empresa/Cliente/Centro/Trabajador creados a través de los cuatro pasos.
///
/// El orden Empresa → Cliente reemplaza al Cliente → Empresa anterior a
/// 2026-09-08: ver el comentario normativo en AltaGuiada.razor.cs sobre por
/// qué ese orden arrastraba la distinción previa a la congelación de
/// terminología (F3b).
/// </summary>
[Collection("AppCollection")]
public class AltaGuiadaTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Asistente_de_alta_guiada_crea_Empresa_Cliente_Centro_y_Trabajador_encadenados()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var razonSocialEmpresa = $"AltaGuiada Empresa {sufijo}";
        var razonSocialCliente = $"AltaGuiada Cliente {sufijo}";
        var nombreCentro = $"AltaGuiada Centro {sufijo}";
        var nombreTrabajador = "Ada";
        var apellidosTrabajador = $"AltaGuiada {sufijo}";

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/alta-guiada");

        // --- Paso 1: Empresa (nueva, no vincular una existente) ---
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "1. Empresa" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        // "CIF (opcional)", no "CIF": la asimetría es real y no un rótulo
        // descuidado — CrearClienteCommand exige el CIF (NotEmpty + NIF de
        // empresa válido) y CrearEmpresaCommand lo acepta nulo. El paso 2
        // sigue pidiendo "CIF" a secas.
        await page.GetByLabel("CIF (opcional)", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_997_702));
        await page.GetByText("Guardar y continuar").ClickAsync();

        // --- Paso 2: Cliente ---
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "2. Cliente" }).WaitForAsync();
        // El resumen del paso demuestra que la Empresa del paso 1 ya está
        // persistida y encadenada — no es solo estado en memoria del wizard.
        // Locator acotado con HasText, no ".texto-vacio-seccion" a secas: el
        // formulario de Cliente nuevo (el camino por defecto) ya pinta un
        // segundo párrafo con esa misma clase ("Prioriza sus vencimientos."),
        // y un locator sin acotar viola el modo estricto de Playwright con
        // dos coincidencias.
        await Expect(page.Locator(".texto-vacio-seccion", new PageLocatorOptions { HasText = razonSocialEmpresa }))
            .ToBeVisibleAsync();

        await page.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await page.GetByLabel("CIF", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_997_701));
        await page.GetByText("Guardar y continuar a Centro").ClickAsync();

        // --- Paso 3: Centro ---
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "3. Centro" }).WaitForAsync();
        await Expect(page.Locator(".texto-vacio-seccion")).ToContainTextAsync(razonSocialEmpresa);
        await Expect(page.Locator(".texto-vacio-seccion")).ToContainTextAsync(razonSocialCliente);

        // Antes de guardar el primer Centro, "Terminar aquí" está visible
        // (_centrosCreados == 0) — confirma el estado de partida del paso.
        await Expect(page.GetByText("Terminar aquí")).ToBeVisibleAsync();

        await page.GetByLabel("Nombre", new PageGetByLabelOptions { Exact = true }).FillAsync(nombreCentro);
        await page.GetByText("Guardar centro").ClickAsync();

        // "Terminar aquí" desaparece (_centrosCreados pasa a 1) — es la señal
        // de que el Centro se guardó de verdad, sin depender de leer un
        // toast.
        //
        // Este comentario decía que "en la práctica el campo Nombre sigue
        // mostrando el valor guardado" pese a que el código lo limpia. Era
        // cierto cuando se escribió (2026-08-14) y dejó de serlo el
        // 2026-09-06: CampoTexto ganó @key="_generacionValor" en #485, que
        // recrea el <input> cuando el padre reescribe el valor por su cuenta
        // — antes, devolver el campo al mismo valor que Blazor ya tenía
        // renderizado no producía diff y el DOM se quedaba con lo tecleado.
        // El paso 3 ya afirma en pantalla que el formulario se ha vaciado.
        await Expect(page.GetByText("Terminar aquí")).Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // --- Paso 4: Trabajadores, encadenado desde el resumen del Centro ---
        await page.GetByText("Continuar a Trabajadores").ClickAsync();
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "4. Trabajadores" }).WaitForAsync();

        await page.GetByLabel("Nombre", new PageGetByLabelOptions { Exact = true }).FillAsync(nombreTrabajador);
        await page.GetByLabel("Apellidos").FillAsync(apellidosTrabajador);
        await page.GetByLabel("DNI / NIE").FillAsync(Ayudas.GenerarDniValido(9_997_703));
        await page.GetByText("Guardar trabajador").ClickAsync();

        // El trabajador queda creado y asignado al Centro del paso 3 sin
        // salir del asistente — confirma en pantalla, no solo por ausencia
        // de error.
        await Expect(page.Locator(".resumen-alta-guiada")).ToContainTextAsync($"{nombreTrabajador} {apellidosTrabajador}");

        // Los cuatro pasos quedan marcados como completados en el stepper.
        var pasosCompletados = page.Locator(".indicador-pasos-item.indicador-pasos-completado");
        await Expect(pasosCompletados).ToHaveCountAsync(4);

        // --- Verificación final: los registros existen de verdad, consultando cada pantalla ---
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
