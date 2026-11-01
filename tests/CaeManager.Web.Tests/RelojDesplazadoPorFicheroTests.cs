using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace CaeManager.Web.Tests;

/// <summary>
/// El reloj del arnés E2E (<see cref="RelojDesplazadoPorFichero"/>) mueve el reloj de la sesión, así que su
/// registro tiene que ser inerte sin la clave y no existir fuera de Development.
/// </summary>
public class RelojDesplazadoPorFicheroTests
{
    private static IConfiguration Configuracion(string? ruta) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(ruta is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [RelojDesplazadoPorFichero.ClaveConfiguracion] = ruta })
            .Build();

    private static HostingEnvironment Entorno(string nombre) => new() { EnvironmentName = nombre };

    [Fact]
    public void Sin_la_clave_no_registra_ningun_reloj()
    {
        var servicios = new ServiceCollection();

        RelojDesplazadoPorFichero.Registrar(servicios, Configuracion(null), Entorno(Environments.Development));

        servicios.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Con_la_clave_fuera_de_Development_el_arranque_lanza(string entorno)
    {
        var accion = () => RelojDesplazadoPorFichero.Registrar(
            new ServiceCollection(), Configuracion(Path.GetTempFileName()), Entorno(entorno));

        accion.Should().Throw<InvalidOperationException>().WithMessage("*solo se admite en Development*");
    }

    [Fact]
    public void En_Development_el_reloj_suma_el_desplazamiento_del_fichero_y_aguanta_un_fichero_ausente()
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"reloj-{Guid.NewGuid():N}.txt");
        var servicios = new ServiceCollection();
        RelojDesplazadoPorFichero.Registrar(servicios, Configuracion(ruta), Entorno(Environments.Development));
        var reloj = servicios.BuildServiceProvider().GetRequiredService<TimeProvider>();

        // Sin fichero: reloj del sistema.
        (reloj.GetUtcNow() - TimeProvider.System.GetUtcNow()).Duration().Should().BeLessThan(TimeSpan.FromSeconds(5));

        try
        {
            File.WriteAllText(ruta, "360");
            var adelantado = reloj.GetUtcNow() - TimeProvider.System.GetUtcNow();
            adelantado.Should().BeCloseTo(TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(5));

            // Si el fichero desaparece o no se entiende, vale el último desplazamiento leído.
            File.WriteAllText(ruta, "no es un numero");
            (reloj.GetUtcNow() - TimeProvider.System.GetUtcNow()).Should().BeCloseTo(TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(5));
            File.Delete(ruta);
            (reloj.GetUtcNow() - TimeProvider.System.GetUtcNow()).Should().BeCloseTo(TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.Delete(ruta);
        }
    }
}
