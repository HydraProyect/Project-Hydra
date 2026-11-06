using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La insignia «Próximamente» común (decisión de Chris, 2026-11-07): una píldora neutra con el texto
/// «Próximamente», que no se clica nunca (un <c>span</c> sin rol ni tabindex) y cuyo texto sale de recursos.
/// </summary>
public class InsigniaProximamenteTests : BunitContext
{
    public InsigniaProximamenteTests() => Services.AddLocalization();

    [Fact]
    public void Es_un_span_con_el_texto_Proximamente_y_su_clase()
    {
        var cut = Render<InsigniaProximamente>();

        var insignia = cut.Find("span");
        insignia.TextContent.Should().Be("Próximamente");
        insignia.ClassList.Should().BeEquivalentTo(["insignia-proximamente"]);
        insignia.GetAttribute("data-pieza").Should().Be("insignia-proximamente");
    }

    [Fact]
    public void No_es_un_control_interactivo()
    {
        var cut = Render<InsigniaProximamente>();

        cut.Markup.Should().NotContainAny("<button", "<a ", "<input", "<select");
        var insignia = cut.Find("span");
        insignia.HasAttribute("role").Should().BeFalse("no lleva rol interactivo");
        insignia.HasAttribute("tabindex").Should().BeFalse("no entra en el orden de tabulación");
        insignia.HasAttribute("onclick").Should().BeFalse();
        insignia.Attributes.Select(a => a.Name).Should().OnlyContain(a => a == "class" || a == "data-pieza" || a.StartsWith("b-"),
            "ni aria-*, ni title, ni manejadores: la ayuda vive en el contexto de quien la usa");
    }

    [Fact]
    public void El_texto_viene_de_recursos_y_se_traduce_al_catalan()
    {
        var anterior = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ca-ES");

            Render<InsigniaProximamente>().Find("span").TextContent.Should().Be("Properament");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = anterior;
        }
    }
}
