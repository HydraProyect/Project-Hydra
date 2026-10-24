using CaeManager.Infrastructure.Email;
using CaeManager.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

internal static class SeguridadCuentaPruebas
{
    public static EnlacesPublicos Enlaces(string? url = "https://cuentas.example/", string entorno = "Production") =>
        new(Options.Create(new SmtpEmailOptions { UrlBasePublica = url }),
            new HostingEnvironment { EnvironmentName = entorno }, NullLogger<EnlacesPublicos>.Instance);

    public static void AddSeguridadCuentaDePrueba(this IServiceCollection servicios)
    {
        // TryAdd: una prueba puede registrar antes una variante (p. ej. sin URL pública).
        servicios.TryAddSingleton<Microsoft.AspNetCore.Identity.ILookupNormalizer, Microsoft.AspNetCore.Identity.UpperInvariantLookupNormalizer>();
        servicios.TryAddSingleton(Enlaces());
        servicios.TryAddSingleton(TimeProvider.System);
        servicios.TryAddSingleton(sp => new RecuperacionContrasena(sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(), NullLogger<RecuperacionContrasena>.Instance));
    }
}