using Bunit;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="SelectorIdioma"/> obedece a <c>Localizacion:CatalanHabilitado</c>
/// (decisión de producto del 2026-09-26): apagado no pinta nada —ni el
/// formulario ni el endónimo «Català»—; encendido, el selector de siempre. El
/// interruptor vive en el propio componente, así que esto cubre cualquier
/// layout que lo monte.
/// </summary>
public class SelectorIdiomaInterruptorCatalanTests : BunitContext
{
    public SelectorIdiomaInterruptorCatalanTests()
    {
        Services.AddLocalization();
    }

    [Fact]
    public void El_catalan_esta_apagado_por_defecto() =>
        new OpcionesLocalizacion().CatalanHabilitado.Should().BeFalse(
            "sin configuración explícita, ningún entorno enseña un idioma a medio traducir");

    [Fact]
    public void Con_el_catalan_apagado_el_selector_no_se_pinta()
    {
        Services.AddSingleton<IOptions<OpcionesLocalizacion>>(Opciones.Create(new OpcionesLocalizacion()));

        var selector = Render<SelectorIdioma>();

        selector.Markup.Trim().Should().BeEmpty();
        selector.FindAll("form").Should().BeEmpty();
        selector.Markup.Should().NotContain("Català");
    }

    [Fact]
    public void Con_el_catalan_encendido_el_selector_ofrece_los_dos_idiomas()
    {
        Services.AddSingleton<IOptions<OpcionesLocalizacion>>(
            Opciones.Create(new OpcionesLocalizacion { CatalanHabilitado = true }));

        var selector = Render<SelectorIdioma>();

        selector.Find("form.selector-idioma-formulario").GetAttribute("action").Should().Be("/cuenta/idioma");
        selector.FindAll("select.selector-idioma option").Select(o => o.GetAttribute("value"))
            .Should().Equal(CulturaUsuarioCookie.CulturaEspanol, CulturaUsuarioCookie.CulturaCatalan);
        selector.Markup.Should().Contain("Català");
    }
}
