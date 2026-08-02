using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

public class AislamientoDeCaeManagerDbContextTests
{
    [Theory]
    [InlineData("CaeManager.Domain")]
    [InlineData("CaeManager.Application")]
    [InlineData("CaeManager.Web")]
    public void Ningun_tipo_fuera_de_Infrastructure_referencia_CaeManagerDbContext(string nombreAssembly)
    {
        var assembly = ReflexionArquitecturaHelper.CargarAssembly(nombreAssembly);
        var infractores = ReflexionArquitecturaHelper.TiposQueReferencianTipo(assembly, typeof(CaeManagerDbContext));

        infractores.Should().BeEmpty(
            "el DbContext concreto es exclusivo de Infrastructure — el resto del sistema depende de las interfaces segregadas por feature");
    }
}
