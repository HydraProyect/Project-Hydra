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

    /// <summary>
    /// Un sustituto suelto (medio emoji pegado en un buscador) no es Unicode válido y
    /// <c>string.Normalize</c> lo rechaza con una excepción. <see cref="TextoDeBusqueda.Contiene"/>
    /// filtra listas dentro del render de una pantalla (/proyectos): no lanza, y la respuesta
    /// fijada es «no lo contiene», esté el sustituto en el término o en el texto. Es coherente con
    /// <see cref="TextoDeBusqueda.Coincidencias"/>, que con esa entrada no devuelve tramos.
    /// (Las cadenas se montan aquí: un sustituto suelto no se puede escribir en un atributo.)
    /// </summary>
    [Fact]
    public void Contiene_con_un_sustituto_suelto_no_lanza_y_responde_que_no()
    {
        var suelto = new string((char)0xD83C, 1);
        var textoRoto = "Obra " + suelto + " rota";

        TextoDeBusqueda.Contiene("Obra", suelto).Should().BeFalse();
        TextoDeBusqueda.Contiene(textoRoto, "obra").Should().BeFalse("con el texto mal formado tampoco casa, aunque el término esté");
        TextoDeBusqueda.Contiene(suelto, suelto).Should().BeFalse();

        TextoDeBusqueda.Coincidencias("Obra", suelto).Should().BeEmpty();
        TextoDeBusqueda.Coincidencias(textoRoto, "obra").Should().BeEmpty();
        TextoDeBusqueda.Coincidencias(suelto, suelto).Should().BeEmpty();
    }

    private static string[] Marcados(string texto, string termino) =>
        [.. TextoDeBusqueda.Coincidencias(texto, termino).Select(tramo => texto[tramo])];

    /// <summary>Los tramos son texto ORIGINAL: se marca «García» aunque se haya escrito «garcia».</summary>
    [Theory]
    [InlineData("García Núñez", "garcia", "García")]
    [InlineData("García Núñez", "GARCÍA", "García")]
    [InlineData("garcia nunez", "Núñez", "nunez")]
    [InlineData("Almacén Logístico", "cen log", "cén Log")]
    [InlineData("Pingüino", "GUI", "güi")]
    [InlineData("12345678Z", "5678", "5678")]
    public void Coincidencias_devuelve_el_tramo_del_texto_original(string texto, string termino, string esperado)
    {
        Marcados(texto, termino).Should().Equal(esperado);
    }

    [Fact]
    public void Coincidencias_devuelve_todas_de_izquierda_a_derecha_sin_solaparse()
    {
        Marcados("Ana Bandana", "an").Should().Equal("An", "an", "an");
        TextoDeBusqueda.Coincidencias("Ana Bandana", "an").Should().Equal(0..2, 5..7, 8..10);
    }

    /// <summary>
    /// «aa» sobre «aaaa» casa en 0 y en 2: son dos coincidencias contiguas y salen como un solo
    /// tramo, para que el resaltado no pinte dos marcas pegadas.
    /// </summary>
    [Fact]
    public void Coincidencias_contiguas_se_funden_en_un_tramo()
    {
        TextoDeBusqueda.Coincidencias("aaaa", "aa").Should().Equal(0..4);
        TextoDeBusqueda.Coincidencias("aaaaa", "aa").Should().Equal(0..4);
        TextoDeBusqueda.Coincidencias("Ana Banana", "an").Should().Equal(0..2, 5..9);
    }

    /// <summary>
    /// Texto descompuesto (NFD): la «n» y su virgulilla combinante U+0303 son dos <c>char</c> y un
    /// solo grafema. El tramo se lleva los dos: una marca combinante nunca queda fuera.
    /// </summary>
    [Fact]
    public void Coincidencias_no_deja_fuera_una_marca_combinante()
    {
        const string descompuesto = "Nun\u0303ez";

        TextoDeBusqueda.Coincidencias(descompuesto, "nu").Should().Equal(0..2);
        TextoDeBusqueda.Coincidencias(descompuesto, "un").Should().Equal([1..4], "la virgulilla va con su «n»");
        TextoDeBusqueda.Coincidencias(descompuesto, "nuñez").Should().Equal(0..6);
        TextoDeBusqueda.Coincidencias(descompuesto, "ez").Should().Equal(4..6);
        // Término descompuesto contra texto precompuesto.
        TextoDeBusqueda.Coincidencias("Núñez", "un\u0303").Should().Equal(1..3);
    }

    /// <summary>Un emoji fuera del plano básico son dos <c>char</c> (par sustituto): el tramo no lo parte.</summary>
    [Fact]
    public void Coincidencias_no_parte_un_par_sustituto()
    {
        const string texto = "Obra 🏗 Núñez";

        TextoDeBusqueda.Coincidencias(texto, "🏗").Should().Equal(5..7);
        Marcados(texto, "a 🏗 n").Should().Equal("a 🏗 N");
        Marcados(texto, "nunez").Should().Equal("Núñez");
    }

    /// <summary>La secuencia de emoji con ZWJ es un solo grafema: casar con una parte marca el grafema entero.</summary>
    [Fact]
    public void Coincidencias_marca_el_grafema_entero()
    {
        const string familia = "👨\u200D👩\u200D👧";

        Marcados($"a{familia}b", "👩").Should().Equal(familia);
    }

    [Theory]
    [InlineData(null, "a")]
    [InlineData("", "a")]
    [InlineData("García", null)]
    [InlineData("García", "")]
    [InlineData("García", "\u0301")] // solo una marca combinante: normalizada queda vacía
    [InlineData("García", "garza")]
    [InlineData("Weiß", "weiss")] // divergencia conocida con unaccent de PostgreSQL
    public void Coincidencias_sin_texto_sin_termino_o_sin_coincidencia_no_devuelve_nada(string? texto, string? termino)
    {
        TextoDeBusqueda.Coincidencias(texto, termino).Should().BeEmpty();
    }

    /// <summary>El término es literal, como en <see cref="TextoDeBusqueda.Contiene"/>.</summary>
    [Fact]
    public void Coincidencias_toma_los_comodines_y_los_espacios_como_texto()
    {
        Marcados("Descuento del 100% en EPI", "100%").Should().Equal("100%");
        TextoDeBusqueda.Coincidencias("Descuento del 1000 en EPI", "100%").Should().BeEmpty();
        Marcados("ref_a", "f_a").Should().Equal("f_a");
        TextoDeBusqueda.Coincidencias("refxa", "f_a").Should().BeEmpty();
        // Un espacio final forma parte del término: la consulta SQL tampoco lo recorta.
        Marcados("García Núñez", "garcia ").Should().Equal("García ");
        TextoDeBusqueda.Coincidencias("García", "garcia ").Should().BeEmpty();
    }

    /// <summary>
    /// Un sustituto suelto no es Unicode válido y <c>string.Normalize</c> puede lanzar. El resaltado
    /// no puede tirar el render de una fila: nunca sale una excepción. (Las cadenas se montan aquí
    /// y no en un <c>InlineData</c>: un sustituto suelto no se puede escribir en un atributo.)
    /// </summary>
    [Fact]
    public void Coincidencias_con_un_sustituto_suelto_no_lanza()
    {
        var suelto = new string((char)0xD83C, 1);

        FluentActions.Invoking(() => TextoDeBusqueda.Coincidencias("Obra " + suelto + " rota", "obra")).Should().NotThrow();
        FluentActions.Invoking(() => TextoDeBusqueda.Coincidencias("Obra", suelto)).Should().NotThrow();
        FluentActions.Invoking(() => TextoDeBusqueda.Coincidencias(suelto, suelto)).Should().NotThrow();
    }

    /// <summary>
    /// Mismo criterio que <see cref="TextoDeBusqueda.Contiene"/>: hay tramos exactamente cuando el
    /// texto contiene el término. Si divergieran, una fila filtrada en memoria saldría sin marca,
    /// o con marca una que el filtro habría quitado.
    /// </summary>
    [Theory]
    [InlineData("García Núñez", "garcia")]
    [InlineData("García Núñez", "garza")]
    [InlineData("Nun\u0303ez", "nuñez")]
    [InlineData("Núñez", "un\u0303")]
    [InlineData("Weiß", "weiss")]
    [InlineData("Søren", "soren")]
    [InlineData("Almacén Logístico", "cen log")]
    [InlineData("Obra 🏗 Núñez", "🏗 n")]
    [InlineData("한국 건설", "한국")]
    [InlineData("\u0301García", "garcia")]
    [InlineData("ref_a", "F_A")]
    [InlineData("İstanbul", "istanbul")]
    public void Coincidencias_y_Contiene_coinciden(string texto, string termino)
    {
        (TextoDeBusqueda.Coincidencias(texto, termino).Count > 0)
            .Should().Be(TextoDeBusqueda.Contiene(texto, termino));
    }
}
