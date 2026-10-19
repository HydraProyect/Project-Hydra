using Bunit;
using CaeManager.Application.Comunicaciones.AlertasCriticas;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class BannerAlertaCriticaTests : BunitContext
{
    public BannerAlertaCriticaTests() => Services.AddLocalization();

    [Fact]
    public void Muestra_el_texto_como_texto_plano_y_las_dos_decisiones()
    {
        bool? decision = null;
        var componente = Render<BannerAlertaCritica>(p => p
            .Add(c => c.Alerta, new AlertaCriticaDto("<script>alert(1)</script> Inspección de Trabajo", EstadoAlertaCritica.Pendiente, true, true, 87))
            .Add(c => c.OnResolver, (bool confirmar) => decision = confirmar));
        componente.Find("[role='alert']").TextContent.Should().Contain("Posible caso crítico").And.Contain("87");
        componente.FindAll("script").Should().BeEmpty();
        componente.Find("blockquote").TextContent.Should().Contain("<script>");
        componente.FindAll("button")[0].Click();
        decision.Should().BeTrue();
        componente.FindAll("button")[1].Click();
        decision.Should().BeFalse();
    }

    [Fact]
    public void Un_Gestor_Cae_ve_el_aviso_sin_botones_de_decision()
    {
        var componente = Render<BannerAlertaCritica>(p => p.Add(c => c.Alerta,
            new AlertaCriticaDto("Denúncia presentada", EstadoAlertaCritica.Pendiente, false, null, null)));
        componente.FindAll("button").Should().BeEmpty();
        componente.Markup.Should().Contain("Denúncia presentada");
    }

    [Fact]
    public void Una_alerta_resuelta_ya_no_muestra_el_banner()
    {
        var componente = Render<BannerAlertaCritica>(p => p.Add(c => c.Alerta,
            new AlertaCriticaDto("Sanción", EstadoAlertaCritica.Descartada, false, null, null)));
        componente.FindAll("[role='alert']").Should().BeEmpty();
    }
}
