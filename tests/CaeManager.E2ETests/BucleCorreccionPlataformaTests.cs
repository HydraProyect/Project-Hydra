using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// P0-9b (FS-02 de la auditoría de flujos sin salida, 2026-09-24): el recorrido
/// completo de una acreditación Rechazada, desde la Bandeja hasta volver a quedar
/// pendiente de subir. Lo que bUnit no ve: que «Corregir en {plataforma}» navega
/// de verdad con el deep-link, que la pestaña Plataforma resuelve la fila con los
/// datos reales, y que guardar el drawer de renovación (RenovarDocumentoCommand)
/// devuelve esa misma acreditación a PendienteDeSubir.
///
/// Usa la Rechazada que siembra CicloDocumentalDatosPruebaSeeder en el Tenant
/// Refrielectric (la única de ese Tenant). La renueva, así que la consume: la base
/// es temporal por ejecución (WebAppFixture) y ningún otro E2E la lee.
/// </summary>
[Collection("AppCollection")]
public class BucleCorreccionPlataformaTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Corregir_una_rechazada_desde_la_bandeja_la_devuelve_a_pendiente_de_subir()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);

        // --- Bandeja: el ítem PlataformaRechazada lleva a su acreditación ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/bandeja");
        // Filtrar por el tipo antes de buscar: la cola pagina por grupo y, según el
        // orden de la siembra, la Rechazada puede quedar fuera de la primera página.
        var chipRechazada = page.Locator("button.bandeja-chip", new PageLocatorOptions { HasText = "Rechazada por plataforma" });
        await chipRechazada.ClickAsync();
        await Expect(chipRechazada).ToHaveAttributeAsync("aria-pressed", "true");

        // La siembra deja una sola Rechazada; se ancla en el tipo y no en el motivo,
        // porque sus dos rechazos comparten instante y el «último» no es estable.
        var tarjeta = page.Locator(".panel-resolver-item", new PageLocatorOptions { HasText = "Rechazada por plataforma" });
        await Expect(tarjeta).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });

        await tarjeta.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameRegex = new("^Corregir en ") }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("pestana=plataforma") && url.Contains("acreditacionId="),
            new PageWaitForURLOptions { Timeout = 15_000 });
        var acreditacionId = new Uri(page.Url).Query.Split('&').Single(p => p.Contains("acreditacionId="))
            .Split('=')[1];

        // --- Pestaña Plataforma: la fila resaltada es la Rechazada, sin «Marcar subido» ---
        var fila = page.Locator($".plataforma-fila-documento[data-acreditacion-id='{acreditacionId}']");
        await Expect(fila).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("plataforma-fila-documento--destacada"),
            new LocatorAssertionsToHaveClassOptions { Timeout = 15_000 });
        await Expect(page.Locator(".plataforma-aviso-destino")).ToBeVisibleAsync();
        await Expect(fila.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { NameRegex = new("^Abrir el portal de ") }))
            .ToHaveAttributeAsync("target", "_blank");
        await Expect(fila.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Marcar subido", Exact = true }))
            .ToHaveCountAsync(0);

        // --- Subir versión corregida: abre el drawer de renovación del Documento ---
        await fila.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Subir versión corregida", Exact = true }).ClickAsync();
        var drawer = page.Locator(".drawer-panel");
        await Expect(drawer.GetByText("Renovar documento")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        await drawer.GetByLabel("Fecha de emisión", new LocatorGetByLabelOptions { Exact = true }).FillAsync(hoy.ToString("yyyy-MM-dd"));
        // La vigencia se deja como la trae el drawer (la del Documento sembrado, que
        // puede ser «No caduca»); la corrección es el archivo nuevo.

        var rutaPdf = Ayudas.GenerarPdfDePruebaEnDisco("version-corregida.pdf");
        try
        {
            await drawer.Locator("input[type=\"file\"]").SetInputFilesAsync(rutaPdf);
            await drawer.GetByText("Archivo adjuntado correctamente.").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            await drawer.Locator(".drawer-pie").GetByText("Guardar").ClickAsync();
            try
            {
                await drawer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
            }
            catch (TimeoutException)
            {
                // El Documento sembrado cambia entre arranques: si el drawer no guarda,
                // el mensaje de validación que lo impide es lo único útil del fallo.
                throw new InvalidOperationException(
                    $"El drawer de renovación no se cerró al guardar. Contenido: {await drawer.InnerTextAsync()}");
            }
        }
        finally
        {
            File.Delete(rutaPdf);
        }

        // --- La misma acreditación vuelve a pendiente de subir y ya se puede marcar subida ---
        await Expect(fila.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Marcar subido", Exact = true }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Expect(fila.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Subir versión corregida", Exact = true }))
            .ToHaveCountAsync(0);
    }
}
