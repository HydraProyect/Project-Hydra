using CaeManager.Application.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// La implementación en memoria de <see cref="TextoDeBusqueda.Contiene"/>, la que usan los
/// buscadores que filtran una lista ya cargada. La traducción a SQL se prueba contra PostgreSQL
/// en <c>BusquedaSinAcentosDeListadosBajoRlsTests</c>.
/// </summary>
public class TextoDeBusquedaTests
{
    [Theory]
    [InlineData("García Núñez", "garcia")]
    [InlineData("García Núñez", "GARCÍA")]
    [InlineData("garcia nunez", "García")]
    [InlineData("GARCÍA NÚÑEZ", "nuñez")]
    [InlineData("Almacén Logístico", "cen log")]
    [InlineData("Pingüino", "PINGUINO")]
    [InlineData("Nuñez", "nunez")] // descompuesto (NFD): «n» + virgulilla combinante U+0303
    [InlineData("Núñez", "nuñez")]
    public void Contiene_ignora_acentos_y_mayusculas(string texto, string termino)
    {
        TextoDeBusqueda.Contiene(texto, termino).Should().BeTrue();
    }

    [Theory]
    [InlineData("García Núñez", "garza")]
    [InlineData("Almacén", "almacenes")]
    public void Contiene_no_encuentra_lo_que_no_esta(string texto, string termino)
    {
        TextoDeBusqueda.Contiene(texto, termino).Should().BeFalse();
    }

    [Fact]
    public void Un_texto_nulo_no_contiene_nada()
    {
        TextoDeBusqueda.Contiene(null, "a").Should().BeFalse();
    }

    /// <summary>
    /// Divergencia conocida con PostgreSQL, fijada para que no cambie sin que nadie lo decida: en
    /// memoria «ß» se queda como está y «ø» también, mientras que <c>unaccent</c> las convierte en
    /// «ss» y «o» (lo mide <c>AnadeBusquedaSinAcentosEnListadosTests</c>). Por eso las consultas
    /// normalizan columna y término en PostgreSQL, y este cuerpo solo sirve a listas ya cargadas.
    /// </summary>
    [Fact]
    public void En_memoria_la_eszett_y_la_o_barrada_no_se_transliteran()
    {
        TextoDeBusqueda.Normalizar("Weiß").Should().Be("WEIß");
        TextoDeBusqueda.Contiene("Weiß", "weiß").Should().BeTrue();
        TextoDeBusqueda.Contiene("Weiß", "weiss").Should().BeFalse();
        TextoDeBusqueda.Contiene("Weiss", "weiß").Should().BeFalse();
        TextoDeBusqueda.Contiene("Søren", "soren").Should().BeFalse();
    }

    /// <summary>El término es literal: «%» y «_» no son comodines, tampoco en memoria.</summary>
    [Fact]
    public void Los_comodines_de_LIKE_se_buscan_como_texto()
    {
        TextoDeBusqueda.Contiene("Descuento del 100% en EPI", "100%").Should().BeTrue();
        TextoDeBusqueda.Contiene("Descuento del 1000 en EPI", "100%").Should().BeFalse();
        TextoDeBusqueda.Contiene("ref_a", "f_a").Should().BeTrue();
        TextoDeBusqueda.Contiene("refxa", "f_a").Should().BeFalse();
    }
}
