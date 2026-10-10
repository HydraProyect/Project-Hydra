using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// Un Chromium por clase para los arneses que ejecutan JS de producción sin aplicación ni base
/// de datos. Solo comparte el proceso del navegador: cada test abre y cierra su propio contexto,
/// porque foco, portapapeles y oyentes de teclado son estado que no debe pasar de un test a otro.
/// </summary>
public sealed class NavegadorSinAplicacionFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
