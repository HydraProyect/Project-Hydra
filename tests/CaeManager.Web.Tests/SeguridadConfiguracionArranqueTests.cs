using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Endurecimiento del arranque (hallazgos S-B-6 y S-B-7, auditoría 2026-10-28):
/// la válvula de identidad exenta de RLS no arranca en Production, y los
/// intervalos de revalidación de la sesión y del circuito tienen techo.
/// </summary>
public class SeguridadConfiguracionArranqueTests
{
    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", false)]
    [InlineData("Development", false)]
    public async Task Identidad_administrativa_se_rechaza_al_arrancar_en_produccion(string entorno, bool falla)
    {
        var constructor = Host.CreateDefaultBuilder()
            .UseEnvironment(entorno)
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Rls:PermitirIdentidadAdministrativaInsegura"] = "true",
                ["ConnectionStrings:CaeManagerDbRuntime"] = "runtime"
            }))
            .ConfigureServices((contexto, _) => InfrastructureServiceCollectionExtensions
                .RechazarIdentidadAdministrativaEnProduccion(contexto.Configuration, contexto.HostingEnvironment));
        if (falla)
        {
            Action construir = () => constructor.Build();
            construir.Should().Throw<InvalidOperationException>().WithMessage("*Rls:PermitirIdentidadAdministrativaInsegura*RLS*");
        }
        else
        {
            using var host = constructor.Build();
            await host.StartAsync();
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public async Task Produccion_sin_la_valvula_arranca(string? valor)
    {
        using var host = Host.CreateDefaultBuilder()
            .UseEnvironment("Production")
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Rls:PermitirIdentidadAdministrativaInsegura"] = valor
            }))
            .ConfigureServices((contexto, _) => InfrastructureServiceCollectionExtensions
                .RechazarIdentidadAdministrativaEnProduccion(contexto.Configuration, contexto.HostingEnvironment))
            .Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Sesion:IntervaloRevalidacionSegundos", "61")]
    [InlineData("Sesion:IntervaloRevalidacionSegundos", "0")]
    [InlineData("Circuit:RevalidacionIntervaloSegundos", "61")]
    [InlineData("Circuit:RevalidacionIntervaloSegundos", "0")]
    public async Task Intervalos_fuera_de_rango_impiden_el_arranque(string clave, string valor)
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?> { [clave] = valor }))
            .ConfigureServices((contexto, servicios) => servicios.AddIntervalosRevalidacion(contexto.Configuration))
            .Build();
        Func<Task> arrancar = () => host.StartAsync();
        (await arrancar.Should().ThrowAsync<OptionsValidationException>()).Which.Message.Should().Contain(clave);
    }

    [Fact]
    public async Task Sin_configuracion_se_conservan_los_valores_por_defecto()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices((contexto, servicios) => servicios.AddIntervalosRevalidacion(contexto.Configuration))
            .Build();

        await host.StartAsync();
        var intervalos = host.Services.GetRequiredService<IOptions<IntervalosRevalidacionOptions>>().Value;
        intervalos.SesionSegundos.Should().Be(60);
        intervalos.CircuitoSegundos.Should().Be(60);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Sesion:IntervaloRevalidacionSegundos", "60")]
    [InlineData("Circuit:RevalidacionIntervaloSegundos", "60")]
    [InlineData("Circuit:RevalidacionIntervaloSegundos", "1")]
    public async Task Los_limites_del_rango_se_admiten(string clave, string valor)
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?> { [clave] = valor }))
            .ConfigureServices((contexto, servicios) => servicios.AddIntervalosRevalidacion(contexto.Configuration))
            .Build();

        await host.StartAsync();
        await host.StopAsync();
    }
}
