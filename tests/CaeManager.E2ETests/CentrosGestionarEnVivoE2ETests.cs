using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Cubre el flujo de demo con mayor valor narrativo identificado en la
/// convergencia pre-cliente (2026-08-27): <c>/centros → expandir → semáforo
/// → «Actualizar en Trabajador 360» → subir en la ficha → volver y ver el
/// cambio de estado</c>. A diferencia del flujo de altas
/// (login→cliente→empresa→trabajador→documento), este enseña el valor
/// central de TALVEG en segundos. No existía ningún E2E de esta pantalla
/// hasta ahora; este cierra ese hueco (checklist Demo-Ready, punto 9).
///
/// Contrato vigente desde el 2026-10-19: desde un Centro no se sube nada. Si el
/// documento falta en TALVEG (Pendiente), la acción de la fila es el enlace
/// <c>[data-accion=actualizar-en-ficha]</c>, que lleva a Trabajador 360; allí se
/// sube. El «Gestionar» de la fila (<c>[data-accion=gestionar-acreditacion]</c>)
/// queda para el documento que ya existe y solo cambia el estado en la plataforma
/// (<c>PanelGestionarAcreditacion</c>), que no es lo que cubre este test. La celda
/// de estado lleva dos líneas, <c>[data-linea=talveg]</c> y <c>[data-linea=plataforma]</c>.
///
/// El cambio de estado se ve al volver a <c>/centros</c>: la pantalla carga el
/// estado real del Centro, así que el recuento de pendientes baja en 1 y la fila
/// del tipo deja de estar Pendiente.
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
    public async Task Centro_expandido_muestra_el_semaforo_y_el_documento_subido_en_Trabajador_360_cambia_el_estado_al_volver()
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

        // --- Paso 0: Tipo de documento requerido para Trabajador ---
        // Sin fila explícita de TipoDocumentoCentro, que el tipo esté
        // requerido basta para que aplique a cualquier centro
        // (ResolucionTipoDocumentoCentro.Aplica).
        //
        // Se elige "Sí, siempre" y no "Solo si aplica" a propósito: solo
        // Requerido == Si cuenta para el cumplimiento, porque la maquinaria
        // que evalúa la condición todavía no existe. Con "Condicional" este
        // centro no saldría en falta y el test dejaría de medir lo que mide.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/tipos-documento");
        await page.GetByText("+ Nuevo tipo").ClickAsync();
        await drawer.GetByLabel("Nombre").FillAsync(nombreTipoDocumento);
        await drawer.GetByLabel("¿Se pide?").SelectOptionAsync("Si");
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        // Crear un tipo con «Sí, siempre» cambia lo que se pide en todos los
        // centros, así que la pantalla lo confirma antes de guardar.
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Guardar y aplicar" }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Paso 1: Empresa → Cliente → Centro (alta guiada) ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/alta-guiada");
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "1. Empresa" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        // Paso 1: "Identificación fiscal *": obligatoria, igual que en el paso 2.
        await page.GetByLabel("Identificación fiscal *", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_994_602));
        await page.GetByText("Guardar y continuar").ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "2. Cliente" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await page.GetByLabel("Identificación fiscal *", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_994_601));
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
        await page.GetByPlaceholder("Filtrar esta pantalla: nombre, DNI o alias").FillAsync(apellidosTrabajador);
        var filaTrabajador = page.Locator(".marco-lista-trabajadores .tarjeta-fila-acordeon", new PageLocatorOptions { HasText = apellidosTrabajador });
        await filaTrabajador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        // La fila ya estaba en la lista sin filtrar, así que verla no dice que el filtro haya llegado; y la
        // recarga del filtro limpia la selección. La barrera es la lista filtrada: queda solo esa fila.
        await Expect(page.Locator(".marco-lista-trabajadores .fila-pulsable")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });

        // Quedan recargas de la lista en vuelo tras el alta del Trabajador y el filtro; al aterrizar limpian la selección.
        // Se espera a que el DOM esté quieto 1,5 s (condición, no reloj) antes de seleccionar.
        await page.WaitForFunctionAsync("""
            () => new Promise(resolver => {
                let temporizador;
                const observador = new MutationObserver(() => { clearTimeout(temporizador); temporizador = setTimeout(fin, 1500); });
                const fin = () => { observador.disconnect(); resolver(true); };
                observador.observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
                temporizador = setTimeout(fin, 1500);
            })
            """, null, new PageWaitForFunctionOptions { Timeout = 30_000 });
        // El ☑ de la cabecera de la lista (rediseño de listados, fase 1): un icono con nombre accesible, sin texto visible.
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Selección múltiple", Exact = true }).ClickAsync();
        // Una recarga de la lista posterior al alta del Trabajador limpia la selección (CargarListaAsync sin conservarSeleccion):
        // se marca y se abre «Asignar a un centro» con un tiempo acotado; si la recarga borró la selección, la barra de lote
        // desaparece, el clic agota el tiempo y se vuelve a marcar. Hipótesis de causa (no demostrada): la recarga propia
        // del alta aterriza después del filtro.
        for (var intento = 1; ; intento++)
        {
            var casilla = filaTrabajador.Locator("input[type=\"checkbox\"]");
            if (!await casilla.IsCheckedAsync()) await casilla.CheckAsync();
            try
            {
                await page.Locator(".barra-acciones-lote").GetByText("Asignar a un centro")
                    .ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                break;
            }
            catch (TimeoutException) when (intento < 3) { }
        }
        await page.Locator(".modal-cuerpo").GetByLabel("Centro", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync($"{nombreCentro} ({razonSocialCliente})");
        await page.WaitForTimeoutAsync(500);
        // El botón dice "Asignar igualmente" cuando al comprobarlo faltaba
        // algún documento de los que se piden — exactamente el caso de este
        // test, que necesita esa Falta para poder demostrar luego cómo se
        // resuelve.
        await page.Locator(".modal-pie").GetByText(new System.Text.RegularExpressions.Regex("^Asignar")).ClickAsync();
        await page.Locator(".modal-pie").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Paso 4: /centros — buscar, expandir el Centro y el Trabajador, fila del tipo Pendiente ---
        var (motivoPendientes, filaDocumento) = await AbrirFilaDocumentoAsync(
            page, fixture.BaseUrl, nombreCentro, nombreTrabajador, apellidosTrabajador, nombreTipoDocumento);

        // El catálogo de tipos de documento es compartido por toda
        // "AppCollection" — el Trabajador nuevo también hereda como Faltantes
        // los tipos obligatorios ya sembrados (Apto médico, EPIs, Formación
        // Art. 19, Información Art. 18, DNI/NIE), así que el recuento no es
        // "1": se captura el valor real de partida y se compara contra sí
        // mismo tras resolver únicamente el tipo de este test, en vez de
        // asumir un número fijo.
        // El recuento de pendientes del Centro (Faltante) es su propio motivo
        // bajo la pastilla de estado («1 pendiente»), separado de los vencidos
        // desde el 2026-10-10 (ver AbrirFilaDocumentoAsync).
        var pendientesAntes = await LeerRecuentoAsync(motivoPendientes);
        Assert.True(pendientesAntes >= 1, "El Trabajador recién asignado debería tener al menos el Faltante de este test.");

        // La celda de estado lleva dos líneas (regla del 2026-10-19): la vigencia en TALVEG y la
        // acreditación en las plataformas del Centro. Aquí se observa la de TALVEG.
        var lineaTalveg = filaDocumento.Locator("[data-linea=talveg]");

        // Se ancla por clase Y por texto, y las dos mitades hacen falta:
        //
        // - Por clase, porque la misma palabra aparece también dentro de la
        //   ventana de contexto que envuelve al badge, y GetByText la
        //   resolvía dos veces. La clase es "badge-peligro" desde que
        //   "Pendiente" dejó de pintarse en tono neutro: un requisito sin
        //   documento no es un estado benigno.
        // - Por texto, porque "badge-peligro" ya no identifica el caso: un
        //   documento presente pero Vencido o Urgente lleva esa misma
        //   clase. Distinguir "no existe" de "existe y está vencido" es
        //   justamente lo que separa este badge, y sin la aserción de texto
        //   el test dejaría de observar esa distinción.
        // Desde el 2026-10-08 el estado Faltante se rotula «Pendiente» en toda la interfaz.
        var badgeFalta = lineaTalveg.Locator(".badge-peligro");
        await Expect(badgeFalta).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Expect(badgeFalta).ToHaveTextAsync("Pendiente", new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });

        // --- Paso 5: desde el Centro no se sube nada — la acción lleva a Trabajador 360 ---
        // Falta en TALVEG → la acción de la fila es el enlace a la ficha, no «Gestionar»
        // (ese abre el panel que solo cambia el estado en la plataforma y exige un documento).
        var enlaceFicha = filaDocumento.Locator("[data-accion=actualizar-en-ficha]");
        await Expect(enlaceFicha).ToHaveTextAsync("Actualizar en Trabajador 360", new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });
        await Expect(filaDocumento.Locator("[data-accion=gestionar-acreditacion]")).ToHaveCountAsync(0);

        await enlaceFicha.ClickAsync();
        var rutaFicha = new System.Text.RegularExpressions.Regex(
            @"/trabajadores/(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(\?.*)?$");
        await page.WaitForURLAsync(rutaFicha, new PageWaitForURLOptions { Timeout = 15_000 });
        Assert.True(Guid.TryParse(rutaFicha.Match(page.Url).Groups["id"].Value, out _),
            $"La acción debería llevar a /trabajadores/{{guid}}, pero la URL es {page.Url}.");

        // --- Paso 6: subir el documento de ese tipo en Trabajador 360 (pestaña Centros) ---
        // El Centro llega plegado: se despliega y se pulsa el «Gestionar» del documento que falta,
        // que abre «Nuevo documento» con el Trabajador y el tipo ya elegidos.
        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { NameRegex = new System.Text.RegularExpressions.Regex("^Centros") }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = nombreCentro, Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Gestionar el documento «{nombreTipoDocumento}»", Exact = true }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        var hoy = Ayudas.HoyDeNegocio();
        await drawer.GetByLabel("Fecha de emisión", new LocatorGetByLabelOptions { Exact = true }).FillAsync(hoy.ToString("yyyy-MM-dd"));
        await drawer.GetByLabel("Fecha de vencimiento").FillAsync(hoy.AddDays(180).ToString("yyyy-MM-dd"));

        var rutaPdf = Ayudas.GenerarPdfDePruebaEnDisco();
        try
        {
            await drawer.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaPdf);
            await drawer.GetByText("Archivo adjuntado correctamente.").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            // La ficha ofrece «Guardar y siguiente» como primario mientras queden otros documentos con
            // problema (el catálogo compartido deja varios Pendientes), y «Guardar» a secas, que guarda y
            // cierra, como secundario; sin serie, «Guardar» es el primario. El rol exacto sirve a las dos.
            await drawer.Locator(".drawer-pie").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Guardar", Exact = true }).ClickAsync();
            await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        }
        finally
        {
            File.Delete(rutaPdf);
        }

        // --- Paso 7: volver a /centros — el estado real del Centro ya refleja el documento ---
        // Prueba central del flujo: la pantalla carga el estado del Centro al entrar, así que si el
        // documento subido en la ficha no cambia esta fila ni el recuento, el cálculo del Centro está
        // roto. El recuento global baja en exactamente 1 (el resto de tipos obligatorios del catálogo
        // compartido siguen pendientes, así que no llega a desaparecer del todo).
        var (motivoDespuesDeSubir, filaDespuesDeSubir) = await AbrirFilaDocumentoAsync(
            page, fixture.BaseUrl, nombreCentro, nombreTrabajador, apellidosTrabajador, nombreTipoDocumento);
        var lineaTalvegDespues = filaDespuesDeSubir.Locator("[data-linea=talveg]");
        await Expect(lineaTalvegDespues).ToContainTextAsync("Vigente", new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });
        await Expect(lineaTalvegDespues).Not.ToContainTextAsync("Pendiente");
        await Expect(lineaTalvegDespues.Locator(".badge-peligro")).ToHaveCountAsync(0);
        await Expect(filaDespuesDeSubir.Locator("[data-accion=actualizar-en-ficha]")).ToHaveCountAsync(0);
        var pendientesDespues = await LeerRecuentoAsync(motivoDespuesDeSubir);
        Assert.Equal(pendientesAntes - 1, pendientesDespues);
    }

    /// <summary>
    /// <c>/centros</c> → buscar el Centro, dejar que el buscador asiente, expandir el Centro y después
    /// la fila del Trabajador, hasta tener a la vista la fila del documento de ese tipo. Devuelve el
    /// motivo de pendientes del Centro y la fila del documento. Se usa antes y después de subir el
    /// documento en Trabajador 360: la segunda vez es una carga nueva de la pantalla.
    /// </summary>
    private static async Task<(ILocator MotivoPendientes, ILocator FilaDocumento)> AbrirFilaDocumentoAsync(
        IPage page, string baseUrl, string nombreCentro, string nombreTrabajador, string apellidosTrabajador, string nombreTipoDocumento)
    {
        await Ayudas.NavegarYEsperarAsync(page, $"{baseUrl}/centros");
        var buscador = page.GetByPlaceholder("Filtrar esta pantalla: Centro, código, Cliente o Empresa");
        await buscador.FillAsync(nombreCentro);

        var botonExpandir = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Asignaciones de {nombreCentro}" });
        await botonExpandir.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        // El recuento de pendientes del Centro (Faltante) es su propio motivo
        // bajo la pastilla de estado («1 pendiente»), separado de los vencidos
        // desde el 2026-10-10. Se localiza por su clase propia y no por
        // .badge-peligro, que en la cabecera de la fila es la pastilla de
        // estado y dentro del acordeón, el badge de cada Trabajador.
        var filaCentro = page.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = nombreCentro });
        var motivoPendientes = filaCentro.Locator(".tarjeta-fila-acordeon-cabecera .motivo-recuento-pendientes");
        await Expect(motivoPendientes).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

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
        // del botón que despliega esa fila.
        var botonExpandirTrabajador = filaCentro.Locator(".fila-trabajador-gestionar");
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
        // obligatorios del catálogo compartido también aparecen como "Pendiente"
        // para este Trabajador nuevo, así que no basta con buscar ese texto
        // suelto en toda la tabla.
        var filaDocumento = tablaDocumentos.Locator(".fila-documento-requerido", new LocatorLocatorOptions { HasText = nombreTipoDocumento });
        return (motivoPendientes, filaDocumento);
    }

    /// <summary>La cifra con la que empieza el motivo («6 vencidos» → 6). Falla si el texto no empieza por un número.</summary>
    private static async Task<int> LeerRecuentoAsync(ILocator motivo)
    {
        var texto = (await motivo.InnerTextAsync()).Trim();
        var cifra = new string(texto.TakeWhile(char.IsDigit).ToArray());
        Assert.True(cifra.Length > 0, $"El motivo «{texto}» no empieza por una cifra.");
        return int.Parse(cifra);
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
