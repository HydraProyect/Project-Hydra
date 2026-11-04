using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Plataforma;
using CaeManager.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Issue #2001: <c>AddApplication()</c> registra con TryAdd unos valores por defecto en fallo cerrado
/// (<see cref="CoordenadaSesionPrivilegiadaAusente"/>, <c>RegistroLecturaSesionPrivilegiadaNoDisponible</c>,
/// <c>SesionPrivilegiadaAusente</c>) para que el pipeline de MediatR se construya en cualquier host. En
/// la aplicación web tienen que ganar las piezas de Infrastructure: con la coordenada ausente, el
/// behavior dejaría pasar una lectura con la sesión seleccionada en el circuito pero sin resolver; con
/// el registro no disponible, el Importador no funcionaría bajo Sesión Privilegiada. Este test compone
/// en el orden de <c>Program.cs</c> y fija a qué resuelve cada servicio.
/// </summary>
public class ComposicionDelRegistroDeLecturaPrivilegiadaTests
{
    [Fact]
    public void Program_compone_Application_antes_que_Infrastructure_y_no_vuelve_a_registrar_estas_piezas()
    {
        var programa = File.ReadAllText(Path.Combine(Raiz(), "src", "CaeManager.Web", "Program.cs"));

        var application = programa.IndexOf("builder.Services.AddApplication();", StringComparison.Ordinal);
        var infrastructure = programa.IndexOf("builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);", StringComparison.Ordinal);
        application.Should().BeGreaterThanOrEqualTo(0, "Program.cs compone la capa Application");
        infrastructure.Should().BeGreaterThan(application,
            "Infrastructure se registra después, para que sus implementaciones ganen a los TryAdd de Application");

        programa.Should().NotContain(nameof(ICoordenadaSesionPrivilegiada))
            .And.NotContain(nameof(IRegistroLecturaSesionPrivilegiada))
            .And.NotContain(nameof(ISesionPrivilegiadaActual),
                "la aplicación web no sustituye después las piezas que este test fija");
    }

    [Theory]
    [InlineData(typeof(ICoordenadaSesionPrivilegiada), "CaeManager.Infrastructure.Plataforma.SesionPrivilegiadaActual")]
    [InlineData(typeof(IRegistroLecturaSesionPrivilegiada), "CaeManager.Infrastructure.Plataforma.RegistroLecturaSesionPrivilegiada")]
    [InlineData(typeof(ISesionPrivilegiadaActual), "CaeManager.Infrastructure.Plataforma.SesionPrivilegiadaActual")]
    public void En_la_composicion_real_resuelve_a_la_implementacion_de_Infrastructure(Type servicio, string implementacion)
    {
        var servicios = ComponerComoProgram();

        var registros = servicios.Where(d => d.ServiceType == servicio).ToList();
        registros.Should().HaveCountGreaterThanOrEqualTo(2,
            "el instrumento tiene que ver el valor por defecto de Application y el de Infrastructure");

        // El contenedor resuelve GetRequiredService con el último registro.
        var efectivo = registros[^1];
        efectivo.Lifetime.Should().Be(ServiceLifetime.Scoped, "la sesión y su registro son por ámbito (circuito)");
        efectivo.ImplementationType.Should().NotBeNull("se registra por tipo, sin fábrica que pueda devolver otro")
            .And.Subject!.FullName.Should().Be(implementacion);

        registros.Take(registros.Count - 1)
            .Select(d => d.ImplementationType?.Assembly.GetName().Name)
            .Should().AllBe("CaeManager.Application", "los registros anteriores son los valores por defecto de Application");
    }

    /// <summary>
    /// Las mismas dos llamadas y en el mismo orden que <c>Program.cs</c>, sin <c>WebApplication.CreateBuilder()</c>:
    /// el constructor del host abre vigilantes de ficheros que, en la suite completa, tumbaban el proceso de pruebas.
    /// </summary>
    private static IServiceCollection ComponerComoProgram()
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:CaeManagerDb"] = "Host=localhost;Database=no_se_conecta;Username=x;Password=x",
            ["ConnectionStrings:CaeManagerDbRuntime"] = "Host=localhost;Database=no_se_conecta;Username=x;Password=x",
            // En Production, sin esto el registro de Data Protection se niega por falta de
            // certificado, que aquí no se prueba.
            ["DataProtection:PermitirClavesSinCifrar"] = "true",
        }).Build();

        var servicios = new ServiceCollection();
        servicios.AddSingleton<IConfiguration>(configuracion);
        servicios.AddApplication();
        servicios.AddInfrastructure(configuracion, new EntornoDeProduccion());
        return servicios;
    }

    private sealed class EntornoDeProduccion : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "CaeManager.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string Raiz()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
