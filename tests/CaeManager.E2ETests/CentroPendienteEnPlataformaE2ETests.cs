using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Pendiente en la plataforma CAE del Centro (decisión del 2026-10-10), de punta a punta: un Centro sin plataforma no
/// tiene ese motivo; en cuanto su documento vigente queda sin subir en la plataforma del Centro, la fila de /centros
/// dice «1 pendiente en la plataforma» bajo la pastilla «Pendiente», y la ficha 360 del Centro lo repite. Lo que bUnit
/// no ve: que la consulta real (RLS, alcance, documentos efectivos) llega a esas dos pantallas.
///
/// <para>
/// El escenario se monta como el de <see cref="VisitaCentroGestionadoPorCorreoE2ETests"/>: Empresa, Cliente, Centro,
/// Trabajador y documento con archivo por la interfaz. El canal de plataforma y la acreditación sin subir se escriben
/// por SQL: la interfaz de canales no es lo que se mide, y escribirlos así no dispara ningún envío. No se envía nada.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class CentroPendienteEnPlataformaE2ETests(WebAppFixture fixture)
{
    [Fact]
    public async Task Un_documento_sin_subir_a_la_plataforma_del_Centro_lo_deja_pendiente_en_la_lista_y_en_la_ficha()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var nombreTipoDocumento = $"EnPlataforma Tipo {sufijo}";
        var razonSocialCliente = $"EnPlataforma Cliente {sufijo}";
        var razonSocialEmpresa = $"EnPlataforma Empresa {sufijo}";
        var nombreCentro = $"EnPlataforma Centro {sufijo}";
        var nombreTrabajador = "EnPlataforma";
        var apellidosTrabajador = $"E2E {sufijo}";

        await using var contexto = await fixture.Browser.NewContextAsync();
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
        await page.GetByLabel("Identificación fiscal (opcional)", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_994_802));
        await page.GetByText("Guardar y continuar").ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "2. Cliente" }).WaitForAsync();
        await page.GetByLabel("Razón social").FillAsync(razonSocialCliente);
        await page.GetByLabel("Identificación fiscal", new PageGetByLabelOptions { Exact = true }).FillAsync(Ayudas.GenerarCifValido(9_994_801));
        await page.GetByText("Guardar y continuar a Centro").ClickAsync();

        await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "3. Centro" }).WaitForAsync();
        await page.GetByLabel("Nombre", new PageGetByLabelOptions { Exact = true }).FillAsync(nombreCentro);
        await page.GetByText("Guardar centro").ClickAsync();
        await Expect(page.GetByText("Terminar aquí")).Not.ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // --- Trabajador asignado al Centro ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/trabajadores");
        await page.GetByText("+ Nuevo trabajador").First.ClickAsync();
        var comboEmpresa = drawer.GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Empresa" });
        await page.WaitForTimeoutAsync(300);
        if (await comboEmpresa.IsVisibleAsync())
            await comboEmpresa.SelectOptionAsync(new SelectOptionValue { Label = razonSocialEmpresa });
        else
            await drawer.GetByText(razonSocialEmpresa).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await drawer.GetByLabel("Documento de identidad (DNI, NIE, TIE o pasaporte)").FillAsync(Ayudas.GenerarDniValido(88_100_981));
        await drawer.GetByLabel("Nombre", new LocatorGetByLabelOptions { Exact = true }).FillAsync(nombreTrabajador);
        await drawer.GetByLabel("Apellidos").FillAsync(apellidosTrabajador);
        await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
        await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        await page.GetByPlaceholder("Filtrar esta pantalla: nombre, DNI o alias").FillAsync(apellidosTrabajador);
        var filaTrabajador = page.Locator("tr", new PageLocatorOptions { HasText = apellidosTrabajador });
        await filaTrabajador.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await Expect(page.Locator("tbody tr.fila-pulsable")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Selección múltiple", Exact = true }).ClickAsync();
        await filaTrabajador.Locator("input[type=\"checkbox\"]").CheckAsync();
        await page.Locator(".barra-acciones-lote").GetByText("Asignar a centro…").ClickAsync();
        await page.Locator(".modal-cuerpo").GetByLabel("Centro", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync($"{nombreCentro} ({razonSocialCliente})");
        await page.WaitForTimeoutAsync(500);
        await page.Locator(".modal-pie").GetByText(new System.Text.RegularExpressions.Regex("^Asignar")).ClickAsync();
        await page.Locator(".modal-pie").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // --- Documento vigente, subido desde /centros ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        var buscador = page.GetByPlaceholder("Filtrar esta pantalla: centro, código, Cliente o empresa");
        await buscador.FillAsync(nombreCentro);
        var botonExpandir = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Asignaciones de {nombreCentro}" });
        await botonExpandir.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await buscador.BlurAsync();
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
        var rutaPdf = Ayudas.GenerarPdfDePruebaEnDisco($"enplataforma-{sufijo}.pdf");
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

        // --- Sin plataforma: la fila no tiene el motivo (control negativo, con barrera: la fila ya está pintada) ---
        var filaCentro = page.Locator(".tarjeta-fila-acordeon", new PageLocatorOptions { HasText = nombreCentro });
        var cabeceraFila = filaCentro.Locator(".tarjeta-fila-acordeon-cabecera");
        await Expect(cabeceraFila.Locator(".ranura-estado-centro")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Expect(cabeceraFila.Locator(".motivo-recuento-pendientes")).ToHaveCountAsync(0);

        // --- El Centro pasa a tener plataforma, con la acreditación de ese documento sin subir ---
        // Tipo 0 = TipoCanalGestion.Plataforma; Estado 0 = PendienteDeSubir y EstadoVigencia 0 = SinConfirmar, como
        // los deja el constructor de AcreditacionDocumentoPlataforma.
        var acreditaciones = await fixture.EjecutarSqlAsync("""
            WITH canal AS (
                INSERT INTO "CanalesGestionDocumental"
                    ("Id", "CentroId", "TenantId", "Tipo", "EsPrincipal", "EstaEliminado", "EtiquetaProposito",
                     "ProveedorPlataformaCaeId", "CreadoEnUtc", "Version")
                SELECT gen_random_uuid(), c."Id", c."TenantId", 0, true, false, 'Gestión general',
                       (SELECT p."Id" FROM "ProveedoresPlataformaCae" p WHERE p."Activo" ORDER BY p."Codigo" LIMIT 1),
                       now(), gen_random_uuid()
                FROM "Centros" c WHERE c."Nombre" = @centro
                RETURNING "Id", "TenantId")
            INSERT INTO "AcreditacionesDocumentoPlataforma"
                ("Id", "CanalGestionDocumentalId", "DocumentoId", "TenantId", "Estado", "EstadoVigencia",
                 "EstaEliminado", "CreadoEnUtc", "Version")
            SELECT gen_random_uuid(), canal."Id", d."Id", canal."TenantId", 0, 0, false, now(), gen_random_uuid()
            FROM canal
            JOIN "Documentos" d ON d."TenantId" = canal."TenantId" AND NOT d."EstaEliminado"
            JOIN "Trabajadores" t ON t."Id" = d."TrabajadorId" AND t."Apellidos" = @apellidos
            JOIN "TiposDocumento" td ON td."Id" = d."TipoDocumentoId" AND td."Nombre" = @tipo
            """, ("centro", nombreCentro), ("apellidos", apellidosTrabajador), ("tipo", nombreTipoDocumento));
        Assert.Equal(1, acreditaciones);

        // --- /centros: «Pendiente» con su motivo «1 pendiente en la plataforma» ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros");
        await buscador.FillAsync(nombreCentro);
        var motivoPendientes = cabeceraFila.Locator(".motivo-recuento-pendientes");
        await Expect(motivoPendientes).ToHaveTextAsync("1 pendiente en la plataforma", new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });
        await Expect(cabeceraFila.Locator(".ranura-estado-centro")).ToContainTextAsync("Pendiente");
        var ventana = cabeceraFila.Locator(".ventana-contexto", new LocatorLocatorOptions { Has = page.Locator(".motivo-recuento-pendientes") });
        await Expect(ventana).ToHaveAttributeAsync("aria-label", "1 pendiente en la plataforma. 1 documento pendiente en la plataforma");
        await Expect(ventana.Locator(".ventana-linea")).ToHaveTextAsync(
            $"{nombreTipoDocumento} — {nombreTrabajador} {apellidosTrabajador} — sin subir a la plataforma");

        // --- Ficha 360 del Centro: el mismo recuento junto al título ---
        await buscador.BlurAsync();
        await page.WaitForTimeoutAsync(500);
        await botonExpandir.ClickAsync();
        await Expect(botonExpandir).ToHaveAttributeAsync("aria-expanded", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 15_000 });
        await filaCentro.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Ver el centro completo →" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/centros/"), new PageWaitForURLOptions { Timeout = 15_000 });
        await Expect(page.Locator("[data-recuento='pendientes-plataforma']"))
            .ToHaveTextAsync("1 pendiente en la plataforma", new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });
    }
}
