using System.IO.Compression;
using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// P1-X1: una Visita a un Centro que se gestiona por correo ofrece, sin conexión
/// M365, el texto de la solicitud de acceso para copiar y el zip con la
/// documentación vigente para descargar y adjuntar a mano.
///
/// La semilla no tiene ningún Centro gestionado solo por correo (sus canales de
/// correo conviven siempre con una plataforma principal), así que el test monta el
/// suyo: Empresa, Cliente, Centro y Trabajador por la interfaz, y el documento
/// subido por el mismo camino que <see cref="CentrosGestionarEnVivoE2ETests"/> —la
/// semilla no guarda archivos, y un documento sin archivo no entra en el zip—. El
/// canal de correo y la Visita se escriben por SQL: la interfaz de canales y el
/// formulario de Visita no son lo que este test mide.
/// </summary>
[Collection("AppCollection")]
public class VisitaCentroGestionadoPorCorreoE2ETests(WebAppFixture fixture)
{
    [Fact]
    public async Task La_visita_copia_la_solicitud_de_acceso_y_descarga_el_zip_con_la_documentacion()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var nombreTipoDocumento = $"PorCorreo Tipo {sufijo}";
        var razonSocialCliente = $"PorCorreo Cliente {sufijo}";
        var razonSocialEmpresa = $"PorCorreo Empresa {sufijo}";
        var nombreCentro = $"PorCorreo Centro {sufijo}";
        var nombreTrabajador = "PorCorreo";
        var apellidosTrabajador = $"E2E {sufijo}";
        var nombreSegundo = "Segundo";
        var apellidosSegundo = $"Apoyo {sufijo}";
        var correoCentro = $"acceso.{sufijo}@correo-simulado.local";

        await using var contexto = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = true });
        await contexto.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        var page = await contexto.NewPageAsync();

        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);
        var drawer = page.Locator(".drawer-panel");

        // --- Tipo de documento requerido ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/tipos-documento");
        await page.GetByText("+ Nuevo tipo").ClickAsync();
        await drawer.GetByLabel("Nombre").FillAsync(nombreTipoDocumento);
        await drawer.GetByLabel("¿Se pide?").SelectOptionAsync("Si");
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Guardar y aplicar" }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Empresa → Cliente → Centro ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/clientes/alta-guiada");
        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "1. Empresa" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialEmpresa);
        await page.GetByLabel("Identificación fiscal (opcional)", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_994_702));
        await page.GetByText("Guardar y continuar").ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "2. Cliente empresarial" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await page.GetByLabel("Identificación fiscal", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_994_701));
        await page.GetByText("Guardar y continuar a Centro").ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "3. Centro" }).WaitForAsync();
        await page.GetByLabel("Nombre", new PageGetByLabelOptions { Exact = true }).FillAsync(nombreCentro);
        await page.GetByText("Guardar centro").ClickAsync();
        await Expect(page.GetByText("Terminar aquí")).Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // --- Trabajador asignado al Centro ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        async Task CrearTrabajadorAsync(string nombre, string apellidos, int semillaDni)
        {
            await page.GetByText("+ Nuevo trabajador").First.ClickAsync();
            var comboEmpresa = drawer.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Empresa" });
            await page.WaitForTimeoutAsync(300);
            if (await comboEmpresa.IsVisibleAsync())
                await comboEmpresa.SelectOptionAsync(new SelectOptionValue { Label = razonSocialEmpresa });
            else
                await drawer.GetByText(razonSocialEmpresa).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            await drawer.GetByLabel("Documento de identidad (DNI, NIE, TIE o pasaporte)").FillAsync(Ayudas.GenerarDniValido(semillaDni));
            await drawer.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombre);
            await drawer.GetByLabel("Apellidos").FillAsync(apellidos);
            await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
            await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
        }

        // El segundo no se asigna al Centro: basta con que esté en la base general del Tenant para
        // ser candidato en la pestaña «Trabajadores» de la visita (misma regla que «Editar visita»).
        await CrearTrabajadorAsync(nombreSegundo, apellidosSegundo, 88_100_972);
        await CrearTrabajadorAsync(nombreTrabajador, apellidosTrabajador, 88_100_971);

        await page.GetByPlaceholder("Filtrar esta pantalla: nombre, DNI o alias").FillAsync(apellidosTrabajador);
        var filaTrabajador = page.Locator("tr", new PageLocatorOptions { HasText = apellidosTrabajador });
        await filaTrabajador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        // El ☑ de la cabecera de la lista (rediseño de listados, fase 1): un icono con nombre accesible, sin texto visible.
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Selección múltiple", Exact = true }).ClickAsync();
        await filaTrabajador.Locator("input[type=\"checkbox\"]").CheckAsync();
        await page.Locator(".barra-acciones-lote").GetByText("Asignar a centro…").ClickAsync();
        await page.Locator(".modal-cuerpo").GetByLabel("Centro", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync($"{nombreCentro} ({razonSocialCliente})");
        await page.WaitForTimeoutAsync(500);
        await page.Locator(".modal-pie").GetByText(new System.Text.RegularExpressions.Regex("^Asignar")).ClickAsync();
        await page.Locator(".modal-pie").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Documento vigente con archivo, subido desde /centros ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        var buscadorCentros = page.GetByPlaceholder("Filtrar esta pantalla: centro, código, Cliente empresarial o empresa");
        await buscadorCentros.FillAsync(nombreCentro);
        var botonExpandir = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Asignaciones de {nombreCentro}" });
        await botonExpandir.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await buscadorCentros.BlurAsync();
        await page.WaitForTimeoutAsync(500);
        await botonExpandir.ClickAsync();
        await Expect(botonExpandir).ToHaveAttributeAsync("aria-expanded", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
        var botonExpandirTrabajador = page.GetByRole(AriaRole.Button,
            new PageGetByRoleOptions { Name = $"Documentos exigidos a {nombreTrabajador} {apellidosTrabajador}" });
        await botonExpandirTrabajador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await page.WaitForTimeoutAsync(300);
        await botonExpandirTrabajador.ClickAsync();
        var filaDocumento = page.GetByRole(AriaRole.Table,
                new PageGetByRoleOptions { Name = $"Documentación exigida a {nombreTrabajador} {apellidosTrabajador}" })
            .Locator(".fila-documento-requerido", new LocatorLocatorOptions { HasText = nombreTipoDocumento });
        await filaDocumento.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Gestionar" }).ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        await drawer.GetByLabel("Fecha de emisión", new LocatorGetByLabelOptions { Exact = true }).FillAsync(hoy.ToString("yyyy-MM-dd"));
        await drawer.GetByLabel("Fecha de vencimiento").FillAsync(hoy.AddDays(180).ToString("yyyy-MM-dd"));
        var rutaPdf = Ayudas.GenerarPdfDePruebaEnDisco($"porcorreo-{sufijo}.pdf");
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

        // --- Canal de correo del Centro y Visita con el Trabajador ---
        // Tipo 1 = TipoCanalGestion.Email; Origen 0 y Atribucion 0 son los
        // primeros valores de sus enumerados, como los deja el constructor.
        var canales = await fixture.EjecutarSqlAsync("""
            INSERT INTO "CanalesGestionDocumental"
                ("Id", "CentroId", "TenantId", "Tipo", "EsPrincipal", "EstaEliminado", "EtiquetaProposito",
                 "EmailsDestinatarios", "NombreContacto", "CreadoEnUtc", "Version")
            SELECT gen_random_uuid(), c."Id", c."TenantId", 1, true, false, 'Gestión general',
                   @correo, 'Marta', now(), gen_random_uuid()
            FROM "Centros" c WHERE c."Nombre" = @centro
            """, ("correo", correoCentro), ("centro", nombreCentro));
        Assert.Equal(1, canales);

        var visitaId = Guid.NewGuid();
        var visitas = await fixture.EjecutarSqlAsync("""
            WITH v AS (
                INSERT INTO "Visitas"
                    ("Id", "CentroId", "TenantId", "FechaInicio", "FechaFin", "Origen", "Atribucion",
                     "NotificadoCliente", "EstaEliminado", "CreadoEnUtc", "Version")
                SELECT @visita::uuid, c."Id", c."TenantId", (now() + interval '3 days')::date, (now() + interval '3 days')::date,
                       0, 0, false, false, now(), gen_random_uuid()
                FROM "Centros" c WHERE c."Nombre" = @centro
                RETURNING "Id", "TenantId")
            INSERT INTO "VisitasTrabajadores" ("Id", "TenantId", "TrabajadorId", "VisitaId")
            SELECT gen_random_uuid(), v."TenantId", t."Id", v."Id"
            FROM v JOIN "Trabajadores" t ON t."TenantId" = v."TenantId" AND t."Apellidos" = @apellidos
            """, ("visita", visitaId.ToString()), ("centro", nombreCentro), ("apellidos", apellidosTrabajador));
        Assert.Equal(1, visitas);

        // --- /visitas: abrir la Visita, copiar la solicitud y descargar el zip ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/visitas");
        await page.GetByPlaceholder("Buscar por centro, titular o empresa…").FillAsync(nombreCentro);
        var filaVisita = page.Locator("tr", new PageLocatorOptions { HasText = nombreCentro });
        await filaVisita.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Ayudas.PulsarAccionDeMenuAsync(filaVisita.Locator(".menu-acciones-disparador"), "Ver");
        // El panel se abre en «Información»; la solicitud y el zip viven en «Documentación».
        await drawer.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Documentación", Exact = true }).ClickAsync();

        var solicitud = drawer.Locator(".visitas-aviso");
        await Expect(drawer.Locator(".visitas-aviso-destinatarios")).ToContainTextAsync(correoCentro, new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });
        await Expect(solicitud).ToContainTextAsync("Buenos días, Marta:");
        await Expect(solicitud).ToContainTextAsync($"{nombreTrabajador} {apellidosTrabajador} ({razonSocialEmpresa})");

        await drawer.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Copiar solicitud" }).ClickAsync();
        var copiado = "";
        for (var intento = 0; intento < 20 && !copiado.StartsWith("Solicitud de acceso", StringComparison.Ordinal); intento++)
        {
            await page.WaitForTimeoutAsync(250);
            copiado = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        }
        // El portapapeles de Windows devuelve los saltos como \r\n; que el texto sale
        // sin \r lo fija SolicitudAccesoCorreoComposicionTests, no este instrumento.
        copiado = copiado.Replace("\r\n", "\n");
        Assert.StartsWith($"Solicitud de acceso — {nombreCentro} — ", copiado);
        Assert.Contains("\n\nBuenos días, Marta:\n", copiado);
        Assert.Contains($"- {nombreTrabajador} {apellidosTrabajador} ({razonSocialEmpresa})", copiado);

        var descarga = await page.RunAndWaitForDownloadAsync(() =>
            drawer.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Descargar ZIP de documentación" }).ClickAsync());
        Assert.EndsWith(".zip", descarga.SuggestedFilename);
        var rutaZip = await descarga.PathAsync();
        using (var zip = ZipFile.OpenRead(rutaZip))
        {
            var entrada = Assert.Single(zip.Entries);
            Assert.Contains(nombreTipoDocumento, entrada.FullName);
            Assert.True(entrada.Length > 0, "el zip lleva el archivo subido, no una entrada vacía");
        }

        // --- Documentación gestionada: estado guardado, no cálculo sobre los documentos ---
        // El documento exigido está vigente y el ZIP ya se descargó: aun así la Visita sigue
        // «Por gestionar» hasta que se envía el paquete o se marca a mano.
        await Expect(filaVisita).ToContainTextAsync("Por gestionar");
        var marcarGestionada = drawer.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Marcar documentación gestionada", Exact = true });
        await marcarGestionada.ClickAsync();
        await Expect(filaVisita).ToContainTextAsync("Gestionada", new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });
        await Expect(filaVisita).Not.ToContainTextAsync("Por gestionar");
        await Expect(marcarGestionada).ToHaveCountAsync(0);

        // --- Pestaña «Trabajadores»: quién entra, añadir desde la lista y quitar con el «menos» ---
        await drawer.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Trabajadores", Exact = true }).ClickAsync();
        var filasTrabajador = drawer.Locator(".visitas-trabajador-fila");
        var listaTrabajadores = drawer.Locator(".visitas-trabajadores-lista");
        await Expect(filasTrabajador).ToHaveCountAsync(1);
        await Expect(listaTrabajadores).ToContainTextAsync($"{nombreTrabajador} {apellidosTrabajador}");
        var quitarAlPrimero = drawer.GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = $"Quitar a {nombreTrabajador} {apellidosTrabajador} de la visita", Exact = true });
        await Expect(quitarAlPrimero).ToBeDisabledAsync();

        await drawer.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Añadir trabajador", Exact = true }).ClickAsync();
        var candidatos = drawer.Locator(".visitas-candidatos");
        // Con un único candidato en el Tenant se añade sin lista; con varios hay que elegirlo.
        await Expect(drawer.Locator(".visitas-candidatos, .visitas-trabajador-fila + .visitas-trabajador-fila")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        if (await candidatos.IsVisibleAsync())
        {
            var buscador = candidatos.GetByPlaceholder("Buscar trabajador");
            await buscador.FillAsync("zzzz-nadie-se-llama-asi");
            await Expect(candidatos).ToContainTextAsync("Ningún trabajador coincide.");
            // Sin mayúsculas: el buscador deja un único candidato, el que este test acaba de crear.
            await buscador.FillAsync(apellidosSegundo.ToLowerInvariant());
            await Expect(candidatos.Locator(".visitas-candidato")).ToHaveCountAsync(1);
            await candidatos.Locator(".visitas-candidato").ClickAsync();
        }

        await Expect(filasTrabajador).ToHaveCountAsync(2, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });
        await Expect(listaTrabajadores).ToContainTextAsync($"{nombreSegundo} {apellidosSegundo}");
        // Cambió quién entra: lo gestionado era para los de antes, la Visita vuelve a «Por gestionar».
        await Expect(filaVisita).ToContainTextAsync("Por gestionar", new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });
        await Expect(marcarGestionada).ToBeVisibleAsync();

        // Con dos, el primero ya se puede quitar: pregunta antes y, al confirmar, sale de la visita.
        await quitarAlPrimero.ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Quitar de la visita", Exact = true }).ClickAsync();
        await Expect(filasTrabajador).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });
        await Expect(listaTrabajadores).Not.ToContainTextAsync(apellidosTrabajador);

        // Lo guardado, no lo pintado: tras recargar la página la visita sigue con un solo trabajador y no es el primero.
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/visitas");
        await page.GetByPlaceholder("Buscar por centro, titular o empresa…").FillAsync(nombreCentro);
        await filaVisita.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Expect(filaVisita).ToContainTextAsync("Por gestionar");
        await Ayudas.PulsarAccionDeMenuAsync(filaVisita.Locator(".menu-acciones-disparador"), "Ver");
        await drawer.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Trabajadores", Exact = true }).ClickAsync();
        await Expect(filasTrabajador).ToHaveCountAsync(1);
        await Expect(listaTrabajadores).Not.ToContainTextAsync(apellidosTrabajador);
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
