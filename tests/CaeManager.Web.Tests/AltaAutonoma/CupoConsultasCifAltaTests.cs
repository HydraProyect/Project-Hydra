using CaeManager.Web.Features.AltaAutonoma.Antiabuso;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// El cupo por CIF del paso «Empresa» (A4): mirarlo no lo gasta; solo <see cref="CupoConsultasCifAlta.Gastar"/>, que la
/// página llama cuando la base hizo la transición (hallazgo M1).
/// </summary>
public class CupoConsultasCifAltaTests
{
    [Fact]
    public void Mirar_si_queda_cupo_no_lo_gasta()
    {
        using var cupo = new CupoConsultasCifAlta(1, TimeSpan.FromDays(1));

        foreach (var _ in Enumerable.Range(0, 10))
            cupo.QuedaCupo("B12345674").Should().BeTrue();
    }

    [Fact]
    public void Gastado_el_cupo_ya_no_queda_y_otro_CIF_no_se_ve_afectado()
    {
        using var cupo = new CupoConsultasCifAlta(2, TimeSpan.FromDays(1));

        cupo.Gastar("B12345674");
        cupo.QuedaCupo("B12345674").Should().BeTrue();
        cupo.Gastar("B12345674");
        cupo.QuedaCupo("B12345674").Should().BeFalse();
        cupo.QuedaCupo("A58818501").Should().BeTrue();
    }

    [Fact]
    public void Gastar_con_el_cupo_ya_agotado_no_lanza()
    {
        using var cupo = new CupoConsultasCifAlta(1, TimeSpan.FromDays(1));

        cupo.Gastar("B12345674");
        var gastar = () => cupo.Gastar("B12345674");

        gastar.Should().NotThrow();
        cupo.QuedaCupo("B12345674").Should().BeFalse();
    }
}
