using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.FileStorage;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// ADR-013 M2 (K-17): la reparación de adjuntos temporales recogía la evidencia Inbound por nombre; desde M2 la recoge
/// porque Inbound la aporta por el puerto de contribución. Si la composición real dejara de registrarla, el barrido dejaría
/// fuera esas claves sin fallar. Program.cs solo puede construir la reparación con <see cref="ReparacionAdjuntosTemporales.Crear"/>
/// (el constructor es interno), así que esto fija la misma vía que usa el modo de mantenimiento.
/// </summary>
public class ReferenciasArchivoEnLaComposicionTests
{
    [Fact]
    public void La_reparacion_creada_desde_la_composicion_real_recoge_la_clave_de_la_evidencia_Inbound()
    {
        var servicios = new ServiceCollection();
        servicios.AddInfrastructure(new ConfigurationBuilder().Build(), new EntornoFalso());
        using var proveedor = servicios.BuildServiceProvider();
        using var scope = proveedor.CreateScope();
        using var db = new DbContext(new DbContextOptionsBuilder().Options);

        var reparacion = ReparacionAdjuntosTemporales.Crear(scope.ServiceProvider, db);

        reparacion.CamposAportados.Select(c => (c.Entidad, c.Campo))
            .Should().Contain((typeof(EvidenciaInbound), "ArchivoUrl"));
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
