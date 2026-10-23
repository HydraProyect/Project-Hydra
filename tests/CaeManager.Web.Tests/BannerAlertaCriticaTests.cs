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
    public void Muestra_el_texto_como_texto_plano_y_la_segunda_opinion_sin_botones_de_decision()
    {
        var componente = Render<BannerAlertaCritica>(p => p
            .Add(c => c.Alerta, new AlertaCriticaDto(Guid.NewGuid(), "<script>alert(1)</script> Inspección de Trabajo", EstadoAlertaCritica.Pendiente, true, true, 87)));
        componente.Find("[role='alert']").TextContent.Should().Contain("Posible caso crítico").And.Contain("87");
        componente.FindAll("script").Should().BeEmpty();
        componente.Find("blockquote").TextContent.Should().Contain("<script>");
        // La decisión vive en AlertaCriticaModal; el aviso de la conversación es pasivo.
        componente.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Un_Gestor_Cae_ve_el_aviso_con_la_nota_de_que_decide_la_Direccion()
    {
        var componente = Render<BannerAlertaCritica>(p => p.Add(c => c.Alerta,
            new AlertaCriticaDto(Guid.NewGuid(), "Denuncia presentada", EstadoAlertaCritica.Pendiente, false, null, null)));
        componente.FindAll("button").Should().BeEmpty();
        componente.Markup.Should().Contain("Denuncia presentada").And.Contain("Pendiente de revisión");
    }

    [Fact]
    public void Quien_puede_decidir_no_ve_la_nota_de_pendiente_porque_le_sale_el_modal()
    {
        var componente = Render<BannerAlertaCritica>(p => p.Add(c => c.Alerta,
            new AlertaCriticaDto(Guid.NewGuid(), "Denuncia presentada", EstadoAlertaCritica.Pendiente, true, null, null)));
        componente.Markup.Should().NotContain("Pendiente de revisión");
    }

    [Fact]
    public void Una_alerta_resuelta_ya_no_muestra_el_banner()
    {
        var componente = Render<BannerAlertaCritica>(p => p.Add(c => c.Alerta,
            new AlertaCriticaDto(Guid.NewGuid(), "Sanción", EstadoAlertaCritica.Descartada, false, null, null)));
        componente.FindAll("[role='alert']").Should().BeEmpty();
    }
}
