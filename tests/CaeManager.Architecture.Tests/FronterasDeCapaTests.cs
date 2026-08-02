using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

public class FronterasDeCapaTests
{
    [Theory]
    [InlineData("CaeManager.Application")]
    [InlineData("CaeManager.Infrastructure")]
    [InlineData("CaeManager.Web")]
    [InlineData("CaeManager.Migrations.PostgreSQL")]
    public void Domain_no_depende_de_otras_capas(string assemblyProhibido)
    {
        var domain = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Domain");
        ReflexionArquitecturaHelper.DependeDeAssembly(domain, assemblyProhibido).Should().BeFalse();
    }

    [Fact]
    public void Application_no_depende_de_un_proveedor_EF_concreto()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");
        ReflexionArquitecturaHelper.DependeDeAssembly(application, "Npgsql").Should().BeFalse();
    }

    [Fact]
    public void Application_no_depende_de_Infrastructure()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");
        ReflexionArquitecturaHelper.DependeDeAssembly(application, "CaeManager.Infrastructure").Should().BeFalse();
    }

    [Fact]
    public void Web_no_referencia_CaeManagerDbContext_fuera_del_composition_root()
    {
        var web = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web");
        var infractores = ReflexionArquitecturaHelper.TiposQueReferencianTipo(web, typeof(CaeManagerDbContext));

        infractores.Should().BeEmpty(
            "Web debe resolver persistencia a través de las interfaces de Application, no del DbContext concreto (salvo Program.cs, fuera del alcance de la reflexión por tipo)");
    }

    [Fact]
    public void Web_no_referencia_el_namespace_Persistence_de_Infrastructure()
    {
        var web = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web");
        var infractores = ReflexionArquitecturaHelper.TiposQueReferencianNamespace(web, "CaeManager.Infrastructure.Persistence");

        infractores.Should().BeEmpty();
    }
}
