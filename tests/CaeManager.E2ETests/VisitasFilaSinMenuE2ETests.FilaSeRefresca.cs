using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Guardar la edición de una Visita sustituye su fila en sitio. Antes se recargaba la lista, que
/// vuelve a la página 1 y limpia la selección múltiple y la fila enfocada; la fecha nueva la
/// enseñaban las dos cosas, así que lo que aquí distingue una de otra es que la fila editada
/// sigue enfocada y marcada.
/// </summary>
public partial class VisitasFilaSinMenuE2ETests
{
    [Fact]
    public async Task Guardar_la_edicion_cambia_la_fila_y_conserva_la_seleccion_y_la_fila_enfocada()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var (page, fila) = await AbrirConUnaVisitaAsync(contexto);
        var fechas = (await fila.Locator("button.nombre-abre-vista-rapida").InnerTextAsync()).Trim();
        var inicio = DateOnly.ParseExact(fechas, "dd/MM/yyyy");
        var fin = inicio.AddDays(1);

        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Selección múltiple", Exact = true }).ClickAsync();
        var casilla = fila.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { NameRegex = new Regex("^Seleccionar la visita ") });
        await casilla.CheckAsync();
        await EnfocarLaFilaConJAsync(page, fila);
        await page.Keyboard.PressAsync("e");
        var formulario = FormularioEdicion(page);
        await Expect(formulario).ToBeVisibleAsync();
        await formulario.GetByLabel("Fecha de fin", new LocatorGetByLabelOptions { Exact = true }).FillAsync(fin.ToString("yyyy-MM-dd"));
        await formulario.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Guardar", Exact = true }).ClickAsync();
        await Expect(formulario).ToBeHiddenAsync();

        // El nombre accesible del botón de la fila acaba en sus fechas: con la de fin nueva es otro.
        // Las barras van como \x2F (ver AbrirConUnaVisitaAsync).
        var fechasEnPatron = $"{inicio:dd/MM/yyyy} – {fin:dd/MM/yyyy}".Replace("/", @"\x2F");
        var filaEditada = page.Locator("tbody tr").Filter(new LocatorFilterOptions
        {
            Has = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                NameRegex = new Regex("^Abrir la vista rápida de la visita a .*, " + fechasEnPatron + "$")
            })
        });
        await Expect(filaEditada).ToHaveCountAsync(1);
        // Una recarga de la lista habría limpiado las dos cosas.
        await Expect(filaEditada).ToHaveClassAsync(new Regex("fila-enfocada"));
        await Expect(filaEditada.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { NameRegex = new Regex("^Seleccionar la visita ") }))
            .ToBeCheckedAsync();
    }
}
