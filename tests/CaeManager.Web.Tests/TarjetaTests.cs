using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="Tarjeta"/> siempre pintaba su título como <c>&lt;h2&gt;</c>, lo
/// que aplana la jerarquía cuando la tarjeta es una subsección bajo un h2 que
/// ya pintó el contenedor (hallado por Codex en Parámetros del sistema y en
/// Roles embebidos en Configuración). <c>NivelTitulo</c> deja pintar h2 a h4
/// sin tocar las clases CSS.
/// </summary>
public class TarjetaTests : BunitContext
{
    [Fact]
    public void Sin_especificar_nivel_el_titulo_sigue_siendo_h2()
    {
        var cut = Render<Tarjeta>(p => p.Add(t => t.Titulo, "Umbrales de alerta"));

        var titulo = cut.Find(".tarjeta-titulo");
        titulo.TagName.Should().Be("H2");
    }

    [Theory]
    [InlineData(2, "H2")]
    [InlineData(3, "H3")]
    [InlineData(4, "H4")]
    public void NivelTitulo_pinta_la_etiqueta_pedida_conservando_la_clase(int nivel, string etiquetaEsperada)
    {
        var cut = Render<Tarjeta>(p => p
            .Add(t => t.Titulo, "Presupuesto de IA")
            .Add(t => t.NivelTitulo, nivel));

        var titulo = cut.Find(".tarjeta-titulo");
        titulo.TagName.Should().Be(etiquetaEsperada);
        titulo.ClassList.Should().Contain("tarjeta-titulo");
        titulo.TextContent.Trim().Should().Be("Presupuesto de IA");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void NivelTitulo_fuera_de_rango_lanza(int nivelInvalido)
    {
        var accion = () => Render<Tarjeta>(p => p
            .Add(t => t.Titulo, "Título")
            .Add(t => t.NivelTitulo, nivelInvalido));

        accion.Should().Throw<ArgumentOutOfRangeException>();
    }
}
