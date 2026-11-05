using CaeManager.Application.AltaAutonoma;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La bandera del alta autónoma (DA-10) está apagada por defecto, se enlaza a la sección
/// <c>AltaAutonoma</c> y ningún appsettings del repositorio la enciende.
/// </summary>
public class AltaAutonomaOptionsTests
{
    [Fact]
    public void Sin_configuracion_la_bandera_esta_apagada()
    {
        Resolver([]).Habilitada.Should().BeFalse();
    }

    [Fact]
    public void La_seccion_AltaAutonoma_la_enciende()
    {
        Resolver(new() { ["AltaAutonoma:Habilitada"] = "true" }).Habilitada.Should().BeTrue();
        Resolver(new() { ["OtraSeccion:Habilitada"] = "true" }).Habilitada.Should().BeFalse();
    }

    [Fact]
    public void Infrastructure_la_enlaza_a_su_seccion()
    {
        var fuente = File.ReadAllText(Path.Combine(
            Raiz(), "src", "CaeManager.Infrastructure", "DependencyInjection", "InfrastructureServiceCollectionExtensions.cs"));

        fuente.Replace(" ", string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty).Should().Contain(
            "services.Configure<CaeManager.Application.AltaAutonoma.AltaAutonomaOptions>("
            + "configuration.GetSection(CaeManager.Application.AltaAutonoma.AltaAutonomaOptions.SeccionConfiguracion));");
    }

    [Fact]
    public void Ningun_appsettings_del_repositorio_la_enciende()
    {
        var ficheros = Directory.GetFiles(Path.Combine(Raiz(), "src"), "appsettings*.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        ficheros.Should().NotBeEmpty("guarda del instrumento: el repositorio tiene appsettings");

        foreach (var fichero in ficheros)
        {
            var configuracion = new ConfigurationBuilder().AddJsonFile(fichero, optional: false).Build();
            configuracion.GetValue<bool>("AltaAutonoma:Habilitada").Should().BeFalse($"{fichero} no puede abrir el registro (DA-10)");
        }
    }

    private static AltaAutonomaOptions Resolver(Dictionary<string, string?> valores)
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        var servicios = new ServiceCollection();
        servicios.Configure<AltaAutonomaOptions>(configuracion.GetSection(AltaAutonomaOptions.SeccionConfiguracion));
        using var proveedor = servicios.BuildServiceProvider();
        return proveedor.GetRequiredService<IOptions<AltaAutonomaOptions>>().Value;
    }

    private static string Raiz()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;
        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
