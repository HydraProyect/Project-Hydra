using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.FileStorage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// ADR-013 M2 (K-17): la reparación de adjuntos temporales recogía la evidencia Inbound por nombre; desde M2 la recoge
/// porque Inbound la aporta por el puerto de contribución. Si la composición real dejara de registrarla, el barrido dejaría
/// fuera esas claves sin fallar: esto fija que la aporta, y que es lo único que se aporta hoy.
/// </summary>
public class ReferenciasArchivoEnLaComposicionTests
{
    [Fact]
    public void La_composicion_real_aporta_a_la_reparacion_la_clave_de_la_evidencia_Inbound()
    {
        var servicios = new ServiceCollection();
        servicios.AddInfrastructure(new ConfigurationBuilder().Build(), new EntornoFalso());
        using var proveedor = servicios.BuildServiceProvider();

        var campos = proveedor.GetServices<IContribucionReferenciasArchivo>().SelectMany(c => c.Campos)
            .Select(c => (c.Entidad, c.Campo)).ToList();

        campos.Should().Equal((typeof(EvidenciaInbound), nameof(EvidenciaInbound.ArchivoUrl)));
    }

    private sealed class EntornoFalso : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CaeManager";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
