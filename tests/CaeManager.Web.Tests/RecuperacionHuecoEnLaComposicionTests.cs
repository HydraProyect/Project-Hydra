using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// #2124: RecuperacionHuecoCorreoHostedService resuelve RecuperacionHuecoCorreoService del contenedor en cada
/// ciclo, y el servicio depende de <see cref="IMensajesExternosIngeridos"/> para no volver a encolar lo ya ingerido.
/// Sus tests lo construyen a mano, así que solo este fija que la composición real de <c>AddInfrastructure</c> registra
/// el puerto: si faltara, la recuperación del hueco fallaría en cada ciclo sin que nada más diera rojo.
/// </summary>
public class RecuperacionHuecoEnLaComposicionTests
{
    [Fact]
    public void La_composicion_real_registra_el_puerto_de_lo_ya_ingerido_sobre_el_repositorio_de_conversaciones()
    {
        var servicios = new ServiceCollection();
        servicios.AddInfrastructure(new ConfigurationBuilder().Build(), new EntornoFalso());

        var registro = servicios.Where(d => d.ServiceType == typeof(IMensajesExternosIngeridos)).Should().ContainSingle().Subject;
        registro.Lifetime.Should().Be(ServiceLifetime.Scoped, "comparte el DbContext, y con él el Tenant y la RLS, del ámbito del ciclo");
        registro.ImplementationType.Should().Be(typeof(ConversacionRepository));
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
