using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre el flujo de demo con mayor valor narrativo identificado en la
/// convergencia pre-cliente (2026-08-27): <c>/centros → expandir → semáforo
/// → gestionar → subir → cambio de estado</c>, todo dentro de la misma
/// pantalla y sin navegar — a diferencia del flujo de altas
/// (login→cliente→empresa→trabajador→documento), este enseña el valor
/// central de TALVEG en segundos. No existía ningún E2E de esta pantalla
/// hasta ahora; este cierra ese hueco (checklist Demo-Ready, punto 9).
///
/// El cambio de estado en vivo depende de <c>AcordeonAsignacionesCentro.OnCambio</c>
/// (ver el propio código): al guardar el documento, el componente recarga su
/// lista y avisa a <c>Centros.razor</c> para que refresque la fila del
/// Centro — este test verifica exactamente esa reacción, sin recargar la
/// página manualmente.
///
/// GAP encontrado al escribir este test, mismo origen que el ya documentado
/// en el PR #285 (F4.2a): <c>CampoTexto.ManejarBlurAsync</c> re-invoca
/// <c>ValorChanged</c> incondicionalmente al perder el foco, incluso si el
/// debounce ya había disparado el mismo valor. En <c>/centros</c> eso
/// re-ejecuta <c>BuscarAsync</c>, que llama <c>_expandidos.Clear()</c>
/// (Centros.razor.cs) — si el buscador pierde el foco justo después de
/// expandir una fila (p. ej. porque el siguiente clic cae en el botón de
/// expandir), la fila se vuelve a colapsar sola. Reproducido de forma
/// intermitente con Playwright. Se estabiliza aquí forzando el blur del
/// buscador ANTES de expandir, no después — mismo criterio que el
/// workaround ya aplicado en <c>VinculacionUsuarioClienteRelacionEmpresarialE2ETests</c>.
/// No se toca el componente compartido en este test.
/// </summary>
[Collection("AppCollection")]
public class CentrosGestionarEnVivoE2ETests(WebAppFixture fixture)
{
    [Fact]
    public async Task Centro_expandido_muestra_el_semaforo_y_gestionar_actualiza_el_estado_sin_navegar()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var nombreTipoDocumento = $"CentrosEnVivo Tipo {sufijo}";
        var razonSocialCliente = $"CentrosEnVivo Cliente {sufijo}";
        var razonSocialEmpresa = $"CentrosEnVivo Empresa {sufijo}";
        var nombreCentro = $"CentrosEnVivo Centro {sufijo}";
        var nombreTrabajador = "CentrosEnVivo";
        var apellidosTrabajador = $"E2E {sufijo}";
        var dniTrabajador = Ayudas.GenerarDniValido(88_100_901);

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        var drawer = page.Locator(".drawer-panel");

        // --- Paso 0: Tipo de documento obligatorio para Trabajador ---
        // Sin fila explícita de TipoDocumentoCentro, EsObligatorio=true basta
        // para que aplique a cualquier centro (ResolucionTipoDocumentoCentro.Aplica).
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/tipos-documento");
        await page.GetByText("+ Nuevo tipo").ClickAsync();
        await drawer.GetByLabel("Nombre").FillAsync(nombreTipoDocumento);
        await drawer.GetByLabel("Obligatorio para todos los clientes").CheckAsync();
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Paso 1: Cliente → Empresa → Centro (alta guiada) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/alta-guiada");
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "1. Cliente" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await page.GetByLabel("CIF", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_994_601));
        await page.GetByText("Guardar y continuar a Empresa").ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "2. Empresa" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        await page.GetByLabel("CIF", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_994_602));
        await page.GetByText("Guardar y continuar a Centro").ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "3. Centro" }).WaitForAsync();
        await page.GetByLabel("Nombre", new PageGetByLabelOptions { Exact = true }).FillAsync(nombreCentro);
        await page.GetByText("Guardar centro").ClickAsync();
        await Expect(page.GetByText("Terminar aquí")).Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // --- Paso 2: Trabajador de esa Empresa, todavía sin documento ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByText("+ Nuevo trabajador").First.ClickAsync();

        var comboEmpresa = drawer.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Empresa" });
        await page.WaitForTimeoutAsync(300);
        if (await comboEmpresa.IsVisibleAsync())
            await comboEmpresa.SelectOptionAsync(new SelectOptionValue { Label = razonSocialEmpresa });
        else
            await drawer.GetByText(razonSocialEmpresa).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        await drawer.GetByLabel("Documento de identidad (DNI, NIE, TIE o pasaporte)").FillAsync(dniTrabajador);
        await drawer.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombreTrabajador);
        await drawer.GetByLabel("Apellidos").FillAsync(apellidosTrabajador);
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Paso 3: Asignar el Trabajador al Centro (queda Faltante) ---
        await page.GetByPlaceholder("Buscar por nombre, apellidos, alias o DNI…").FillAsync(apellidosTrabajador);
        var filaTrabajador = page.Locator("tr", new PageLocatorOptions { HasText = apellidosTrabajador });
        await filaTrabajador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        await page.GetByText("Selección múltiple").ClickAsync();
        await filaTrabajador.Locator("input[type=\"checkbox\"]").CheckAsync();

        await page.Locator(".barra-acciones-lote").GetByText("Asignar a centro…").ClickAsync();
        await page.Locator(".modal-cuerpo").GetByLabel("Centro", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync($"{nombreCentro} ({razonSocialCliente})");
        await page.WaitForTimeoutAsync(500);
        // El botón dice "Asignar igualmente" cuando quedan documentos
        // obligatorios sin cubrir — exactamente el caso de este test, que
        // necesita esa Falta para poder demostrar luego cómo se resuelve.
        await page.Locator(".modal-pie").GetByText(new System.Text.RegularExpressions.Regex("^Asignar")).ClickAsync();
        await page.Locator(".modal-pie").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Paso 4: /centros — buscar, dejar que el buscador asiente y expandir ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        var buscador = page.GetByPlaceholder("Buscar centro, cliente o empresa…");
        await buscador.FillAsync(nombreCentro);

        var botonExpandir = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Asignaciones de {nombreCentro}" });
        await botonExpandir.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // El catálogo de tipos de documento es compartido por toda
        // "AppCollection" — el Trabajador nuevo también hereda como Faltantes
        // los tipos obligatorios ya sembrados (Apto médico, EPIs, Formación
        // Art. 19, Información Art. 18, DNI/NIE), así que el recuento no es
        // "1": se captura el valor real de partida y se compara contra sí
        // mismo tras resolver únicamente el tipo de este test, en vez de
        // asumir un número fijo.
        // Acotado a la cabecera de la fila, no a toda la tarjeta: una vez
        // expandida, la fila del Trabajador dentro del acordeón trae su
        // propio badge con la misma clase .badge-peligro, y el recuento del
        // Centro dejaría de resolver a un único elemento.
        var filaCentro = page.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = nombreCentro });
        var badgeVencidas = filaCentro.Locator(".tarjeta-fila-acordeon-cabecera .badge-peligro");
        await Expect(badgeVencidas).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        var vencidasAntes = int.Parse((await badgeVencidas.InnerTextAsync()).Trim());
        Assert.True(vencidasAntes >= 1, "El Trabajador recién asignado debería tener al menos el Faltante de este test.");

        // Blur explícito del buscador ANTES de expandir — ver el GAP
        // documentado arriba: sin esto, el re-disparo redundante de
        // ValorChanged en el primer clic siguiente (el propio botón de
        // expandir) puede vaciar _expandidos justo después de fijarlo.
        await buscador.BlurAsync();
        await page.WaitForTimeoutAsync(500);

        await botonExpandir.ClickAsync();
        await Expect(botonExpandir).ToHaveAttributeAsync("aria-expanded", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });

        // El acordeón tiene dos niveles: expandir el Centro revela la fila
        // del Trabajador, pero la tabla de documentos exigidos vive detrás
        // de un segundo expandir propio de esa fila.
        var botonExpandirTrabajador = page.GetByRole(AriaRole.Button,
            new PageGetByRoleOptions { Name = $"Documentos exigidos a {nombreTrabajador} {apellidosTrabajador}" });
        await botonExpandirTrabajador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        // El acordeón vuelve a renderizar la fila al terminar CargarAsync —
        // un margen corto evita apuntar a un nodo que Blazor está
        // reemplazando (mismo criterio que FlujoCicloDocumentalTests antes
        // de sus interacciones justo después de una carga asíncrona).
        await page.WaitForTimeoutAsync(300);
        await botonExpandirTrabajador.ClickAsync();

        var tablaDocumentos = page.GetByRole(AriaRole.Table,
            new PageGetByRoleOptions { Name = $"Documentación exigida a {nombreTrabajador} {apellidosTrabajador}" });
        await tablaDocumentos.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // Fila específica de ESTE tipo de documento — el resto de tipos
        // obligatorios del catálogo compartido también aparecen como "Falta"
        // para este Trabajador nuevo, así que no basta con buscar ese texto
        // suelto en toda la tabla.
        var filaDocumento = tablaDocumentos.Locator(".fila-documento-requerido", new LocatorLocatorOptions { HasText = nombreTipoDocumento });
        // Se ancla por clase Y por texto, y las dos mitades hacen falta:
        //
        // - Por clase, porque la misma palabra aparece también dentro de la
        //   ventana de contexto que envuelve al badge, y GetByText la
        //   resolvía dos veces. La clase es "badge-peligro" desde que
        //   "Falta" dejó de pintarse en tono neutro: un requisito sin
        //   documento no es un estado benigno.
        // - Por texto, porque "badge-peligro" ya no identifica el caso: un
        //   documento presente pero Vencido o Urgente lleva esa misma
        //   clase. Distinguir "no existe" de "existe y está vencido" es
        //   justamente lo que separa este badge, y sin la aserción de texto
        //   el test dejaría de observar esa distinción.
        var badgeFalta = filaDocumento.Locator(".badge-peligro");
        await Expect(badgeFalta).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Expect(badgeFalta).ToHaveTextAsync("Falta", new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });

        // --- Paso 5: Gestionar → subir el documento, sin salir de /centros ---
        await filaDocumento.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Gestionar" }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        await drawer.GetByLabel("Fecha de emisión", new LocatorGetByLabelOptions { Exact = true }).FillAsync(hoy.ToString("yyyy-MM-dd"));
        await drawer.GetByLabel("Fecha de vencimiento").FillAsync(hoy.AddDays(180).ToString("yyyy-MM-dd"));

        var rutaPdf = Ayudas.GenerarPdfDePruebaEnDisco();
        try
        {
            await drawer.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaPdf);
            await drawer.GetByText("Archivo adjuntado correctamente.").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
            await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        }
        finally
        {
            File.Delete(rutaPdf);
        }

        // --- Paso 6: cambio de estado en vivo — sin recargar la página ---
        // Esta es la prueba central del flujo: AcordeonAsignacionesCentro
        // recarga su lista y dispara OnCambio hacia Centros.razor al
        // guardar — si la fila de este documento no cambia, el enganche
        // está roto. El recuento global del Centro baja en exactamente 1
        // (el resto de tipos obligatorios del catálogo compartido siguen
        // pendientes, así que no llega a desaparecer del todo).
        await Expect(badgeFalta).Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        var vencidasDespues = int.Parse((await badgeVencidas.InnerTextAsync()).Trim());
        Assert.Equal(vencidasAntes - 1, vencidasDespues);
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
