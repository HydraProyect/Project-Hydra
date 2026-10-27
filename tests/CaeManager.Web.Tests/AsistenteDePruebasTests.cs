using System.Text;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.AsistenteIa;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Web.Features.AsistenteIa;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

public class AsistenteDePruebasTests
{
    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("Development", false)]
    [InlineData("Test", false)]
    public void Rechaza_el_doble_fuera_del_entorno_de_pruebas(string entorno, bool datosPrueba)
    {
        var services = new ServiceCollection();
        var activar = () => services.AddAsistenteDePruebas(Configuracion(true, datosPrueba), new Entorno(entorno));

        activar.Should().Throw<InvalidOperationException>();
        services.Should().BeEmpty("la guarda debe ejecutarse antes de registrar el doble");
        var componer = () => new ServiceCollection().AddInfrastructure(Configuracion(true, datosPrueba), new Entorno(entorno));
        componer.Should().Throw<InvalidOperationException>().WithMessage("El asistente falso (AsistenteIa:ProveedorFalsoActivo) exige*");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    public async Task Sustituye_el_proveedor_sin_red_y_conserva_el_historial_recibido(string entorno)
    {
        var services = new ServiceCollection();
        services.AddTransient<IAsistenteIaService>(_ => throw new InvalidOperationException("Proveedor real invocado"));
        services.AddInfrastructure(Configuracion(true, true), new Entorno(entorno));
        using var provider = services.BuildServiceProvider();

        var respuesta = await provider.GetRequiredService<IAsistenteIaService>()
            .PreguntarAsync([new(RolMensajeChat.Usuario, "Pregunta [DOC_1]")], CancellationToken.None);

        respuesta.EsExitoso.Should().BeTrue();
        respuesta.Valor.Should().StartWith("Respuesta del doble de Vega. Historial recibido: ");
        var recibido = Encoding.UTF8.GetString(Convert.FromBase64String(respuesta.Valor.Split("Historial recibido: ")[1]));
        using var historial = JsonDocument.Parse(recibido);
        historial.RootElement.GetArrayLength().Should().Be(1);
        historial.RootElement[0].GetProperty("Texto").GetString().Should().Be("Pregunta [DOC_1]");
        provider.GetServices<IAsistenteIaService>().Should().ContainSingle();
        provider.GetServices<MarcaAsistenteDePruebas>().Should().ContainSingle();
        // Una clave ficticia encendería la extracción, la detección de correo y el proveedor documental
        // de Anthropic, que sí saldrían a la red: la disponibilidad del chat la da la marca, no la clave.
        provider.GetRequiredService<IOptions<AnthropicOptions>>().Value.ApiKey.Should().BeNullOrEmpty();
        new DisponibilidadAsistente(
                provider.GetRequiredService<IOptions<AnthropicOptions>>(),
                provider.GetRequiredService<IOptions<TypeSafeOptions>>(),
                provider.GetServices<MarcaAsistenteDePruebas>())
            .PreguntasDisponibles.Should().BeTrue();
    }

    [Fact]
    public void Sin_configuracion_no_registra_el_doble_en_produccion()
    {
        var services = new ServiceCollection();
        services.AddAsistenteDePruebas(Configuracion(false, true), new Entorno("Production"));
        services.Should().BeEmpty();
    }

    [Fact]
    public void La_marca_de_documentos_no_activa_el_doble_de_Vega()
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DocumentosIa:ProveedorFalsoActivo"] = "true",
            ["DatosPrueba:Activo"] = "true"
        }).Build();
        var services = new ServiceCollection();
        services.AddAsistenteDePruebas(configuracion, new Entorno("Development"));
        services.Should().BeEmpty();
    }

    [Fact]
    public void Sin_doble_ni_clave_Consulta_PRL_no_esta_disponible()
    {
        new DisponibilidadAsistente(
                Options.Create(new AnthropicOptions()), Options.Create(new TypeSafeOptions()), [])
            .PreguntasDisponibles.Should().BeFalse();
    }

    private static IConfiguration Configuracion(bool activo, bool datosPrueba) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Solo en esta composición sin arrancar: permite alcanzar la guarda del doble en Production.
            ["DataProtection:PermitirClavesSinCifrar"] = "true",
            [AsistenteDePruebasExtensions.ClaveConfiguracion] = activo.ToString(),
            ["DatosPrueba:Activo"] = datosPrueba.ToString()
        }).Build();

    private sealed class Entorno(string nombre) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nombre;
        public string ApplicationName { get; set; } = "Pruebas";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
