using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <see cref="IHostEnvironment"/> explícito para los tests de sembradores.
///
/// <para>
/// No se resuelve del contenedor a propósito: los arneses de arranque montan
/// un <c>ServiceCollection</c> pelado, no un host, así que ahí no hay ningún
/// <see cref="IHostEnvironment"/> registrado. Y aunque lo hubiera, conviene
/// que el entorno sea un dato <b>explícito del test</b>: de él depende que la
/// siembra use las credenciales públicas por defecto o exija las configuradas
/// (ver <c>CredencialesDemo</c>), y eso es justo lo que varios de estos tests
/// quieren fijar.
/// </para>
/// </summary>
public sealed class EntornoDePrueba(string nombre) : IHostEnvironment
{
    /// <summary>El entorno de la mayoría de los tests: la siembra usa los
    /// valores públicos por defecto, igual que en local y en CI.</summary>
    public static EntornoDePrueba Desarrollo { get; } = new("Development");

    public string EnvironmentName { get; set; } = nombre;
    public string ApplicationName { get; set; } = "CaeManager.IntegrationTests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
