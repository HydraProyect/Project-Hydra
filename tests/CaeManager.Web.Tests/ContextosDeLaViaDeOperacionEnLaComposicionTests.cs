using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// PR #2223 (opción A): la vía de Operación del selector de Tenant beneficiario lee la
/// DelegacionTenant del par por <see cref="ITenantsQueryContext"/> además de las asignaciones por
/// <see cref="IOperacionesQueryContext"/>. ContextoExtensionMiddleware, RevalidacionClienteActivoMiddleware,
/// TenantBeneficiarioPorDefectoMiddleware y CurrentUserService los resuelven del contenedor de la
/// petición, así que la composición real de <c>AddInfrastructure</c> debe registrarlos con ámbito de
/// petición: si faltara uno, toda petición que pase por la vía de Operación fallaría en ejecución.
/// </summary>
public class ContextosDeLaViaDeOperacionEnLaComposicionTests
{
    [Theory]
    [InlineData(typeof(ITenantsQueryContext))]
    [InlineData(typeof(IOperacionesQueryContext))]
    public void La_composicion_real_registra_el_contexto_con_ambito_de_peticion(Type contexto)
    {
        var servicios = new ServiceCollection();
        servicios.AddInfrastructure(new ConfigurationBuilder().Build(), new EntornoFalso());

        servicios.Where(d => d.ServiceType == contexto).Select(d => d.Lifetime)
            .Should().ContainSingle().Which.Should().Be(ServiceLifetime.Scoped);
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
