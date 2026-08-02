using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

public class ProhibicionGodInterfaceTests
{
    [Fact]
    public void IApplicationDbContext_no_existe()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");

        application.GetType("CaeManager.Application.Common.IApplicationDbContext").Should().BeNull(
            "se partió en interfaces segregadas por feature (P3-32, docs/business/MATURITY_REVIEW.md); si reaparece, algo volvió a acoplar todos los agregados en un solo contrato");
    }
}
