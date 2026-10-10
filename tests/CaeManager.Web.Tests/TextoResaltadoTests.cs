using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Xunit.Sdk;

namespace CaeManager.Web.Tests;

/// <summary>
/// Resaltado de la coincidencia de búsqueda en las filas de los listados (cierre de listados H,
/// 2026-10-10): la parte del texto que casa con el buscador va en un <c>&lt;mark&gt;</c>.
///
/// <para>
/// Lo que se fija aquí: casa con el criterio del buscador (sin acentos ni mayúsculas), marca
/// texto ORIGINAL, no añade ni quita un carácter (<c>textContent</c> idéntico con y sin marca),
/// sin término deja el DOM como estaba y nunca interpreta como HTML lo que le llega.
/// </para>
///
/// <para>
/// Lo que bUnit NO ve: el color. Que la marca tome <c>--color-resaltado-busqueda</c> y la letra
/// de la celda en los dos temas se mira en el navegador.
/// </para>
/// </summary>
public class TextoResaltadoTests : BunitContext
{
    private IRenderedComponent<TextoResaltado> Pintar(string? texto, string? termino) =>
        Render<TextoResaltado>(p => p.Add(c => c.Texto, texto).Add(c => c.Termino, termino));

    /// <summary>
    /// Lo pintado, con cada marca entre corchetes: «Gar[cía] Núñez». Solo admite texto suelto y
    /// <c>&lt;mark&gt;</c> con texto dentro; cualquier otro nodo es un fallo.
    /// </summary>
    private static string Pintado(IRenderedComponent<TextoResaltado> cut) =>
        string.Concat(cut.Nodes.Select(nodo => nodo switch
        {
            IText texto => texto.Data,
            IElement { LocalName: "mark" } marca when marca.ChildNodes.All(hijo => hijo is IText) => $"[{marca.TextContent}]",
            _ => throw new XunitException($"Nodo inesperado en TextoResaltado: {nodo.NodeType} «{nodo.TextContent}»"),
        }));

    private static string TextoDelDom(IRenderedComponent<TextoResaltado> cut) =>
        string.Concat(cut.Nodes.Select(nodo => nodo.TextContent));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_termino_pinta_solo_el_texto_sin_ningun_elemento(string? termino)
    {
        var cut = Pintar("García Núñez", termino);

        cut.Markup.Should().Be("García Núñez", "sin término el DOM es el de antes: un nodo de texto y nada más");
        cut.FindAll("*").Should().BeEmpty();
    }

    [Fact]
    public void Sin_coincidencia_pinta_solo_el_texto_sin_ningun_elemento()
    {
        var cut = Pintar("García Núñez", "garza");

        cut.Markup.Should().Be("García Núñez");
        cut.FindAll("*").Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sin_texto_no_pinta_nada(string? texto)
    {
        Pintar(texto, "garcia").Markup.Should().BeEmpty();
    }

    [Fact]
    public void Una_coincidencia_va_en_una_marca_con_su_clase()
    {
        var cut = Pintar("García Núñez", "Núñez");

        Pintado(cut).Should().Be("García [Núñez]");
        cut.FindAll("mark").Should().ContainSingle()
            .Which.ClassList.Should().Contain("texto-resaltado", "es la clase a la que TextoResaltado.razor.css da el fondo del token");
    }

    [Fact]
    public void Varias_coincidencias_van_cada_una_en_su_marca()
    {
        var cut = Pintar("Ana Bandana", "an");

        Pintado(cut).Should().Be("[An]a B[an]d[an]a");
        cut.FindAll("mark").Should().HaveCount(3);
    }

    [Theory]
    [InlineData("García Núñez", "garcia", "[García] Núñez")] // escrito sin acento, texto acentuado
    [InlineData("Garcia Nunez", "garcía", "[Garcia] Nunez")] // escrito con acento, texto sin él
    [InlineData("García Núñez", "nunez", "García [Núñez]")]
    [InlineData("Almacén Logístico", "cen log", "Alma[cén Log]ístico")]
    [InlineData("Pingüino", "GUI", "Pin[güi]no")]
    public void Casa_sin_distinguir_acentos_y_marca_el_texto_original(string texto, string termino, string esperado)
    {
        Pintado(Pintar(texto, termino)).Should().Be(esperado);
    }

    [Theory]
    [InlineData("GARCÍA NÚÑEZ", "garcía", "[GARCÍA] NÚÑEZ")]
    [InlineData("garcía núñez", "NÚÑEZ", "garcía [núñez]")]
    [InlineData("b70005012", "B700", "[b700]05012")]
    public void Casa_sin_distinguir_mayusculas(string texto, string termino, string esperado)
    {
        Pintado(Pintar(texto, termino)).Should().Be(esperado);
    }

    /// <summary>
    /// Texto descompuesto (NFD): «n» + virgulilla combinante U+0303. La marca se lleva el grafema
    /// entero y el texto del DOM sigue siendo el original, sin recomponer.
    /// </summary>
    [Fact]
    public void Con_texto_descompuesto_la_marca_no_deja_fuera_la_virgulilla()
    {
        const string descompuesto = "Nun\u0303ez";

        var entero = Pintar(descompuesto, "nuñez");
        Pintado(entero).Should().Be($"[{descompuesto}]");
        TextoDelDom(entero).Should().Be(descompuesto);

        var parcial = Pintar(descompuesto, "un");
        Pintado(parcial).Should().Be("N[un\u0303]ez");
        TextoDelDom(parcial).Should().Be(descompuesto);
    }

    /// <summary>
    /// Lo que llega es texto, venga de un nombre o del buscador: «&lt;b&gt;» sale escrito y no crea
    /// un elemento; «%» y «_» no son comodines.
    /// </summary>
    [Theory]
    [InlineData("Obras <b>&</b> 100%_ok", "<b>&", "Obras [<b>&]</b> 100%_ok")]
    [InlineData("Obras <b>&</b> 100%_ok", "&", "Obras <b>[&]</b> 100%_ok")]
    [InlineData("Obras <b>&</b> 100%_ok", "%_", "Obras <b>&</b> 100[%_]ok")]
    [InlineData("Obras <b>&</b> 100%_ok", "0%", "Obras <b>&</b> 10[0%]_ok")]
    [InlineData("Obras <b>&</b> 100%_ok", "<script>alert(1)</script>", "Obras <b>&</b> 100%_ok")]
    [InlineData("Obras 1000 ok", "100%", "Obras 1000 ok")]
    [InlineData("ref-xa", "f_a", "ref-xa")]
    [InlineData("García (obra)", ".*", "García (obra)")]
    [InlineData("García (obra)", "(o", "García [(o]bra)")]
    public void Los_caracteres_especiales_se_pintan_y_se_buscan_como_texto(string texto, string termino, string esperado)
    {
        var cut = Pintar(texto, termino);

        Pintado(cut).Should().Be(esperado);
        cut.FindAll("b, script").Should().BeEmpty("el texto nunca se interpreta como HTML");
    }

    /// <summary>
    /// La marca no añade ni quita un carácter: el <c>textContent</c> es el texto original. De eso
    /// dependen el nombre accesible calculado del contenido y los tests que buscan la fila por su texto.
    /// </summary>
    [Theory]
    [InlineData("García Núñez", null)]
    [InlineData("García Núñez", "garcia")]
    [InlineData("García Núñez", "a")]
    [InlineData("García Núñez", "García Núñez")]
    [InlineData("García Núñez", "garza")]
    [InlineData("  García  Núñez  ", "cia  nu")]
    [InlineData("  García  Núñez  ", " ")]
    [InlineData("Nun\u0303ez", "nunez")]
    [InlineData("Nun\u0303ez", "e")]
    [InlineData("Obra 🏗 Núñez", "🏗")]
    [InlineData("Obra 🏗 Núñez", "a 🏗 n")]
    [InlineData("Obras <b>&</b> 100%_ok", "<b>&")]
    [InlineData("aaaa", "aa")]
    [InlineData("12345678Z", "5678z")]
    public void El_texto_del_DOM_es_el_original_y_ninguna_marca_esta_vacia(string texto, string? termino)
    {
        var cut = Pintar(texto, termino);

        TextoDelDom(cut).Should().Be(texto);
        cut.FindAll("mark").Where(marca => marca.TextContent.Length == 0).Should().BeEmpty("ninguna marca va vacía");
        Pintado(cut).Replace("[", string.Empty).Replace("]", string.Empty).Should().Be(texto,
            "fuera de las marcas solo hay texto suelto");
    }

    /// <summary>Un término de solo espacios no busca (la pantalla lo manda como «sin búsqueda»), así que no marca.</summary>
    [Fact]
    public void Un_termino_de_solo_espacios_no_marca_los_espacios_del_texto()
    {
        Pintar("García Núñez", " ").FindAll("mark").Should().BeEmpty();
    }

    /// <summary>
    /// El término se toma tal cual, sin recortar, como lo recibe la consulta: «garcia » con su
    /// espacio final casa con «García Núñez» y no con «García».
    /// </summary>
    [Fact]
    public void El_termino_no_se_recorta()
    {
        Pintado(Pintar("García Núñez", "garcia ")).Should().Be("[García ]Núñez");
        Pintar("García", "garcia ").FindAll("mark").Should().BeEmpty();
    }

    [Fact]
    public void Al_cambiar_el_termino_la_marca_se_mueve_y_al_vaciarlo_desaparece()
    {
        var cut = Pintar("García Núñez", "garcia");
        Pintado(cut).Should().Be("[García] Núñez");

        cut.Render(p => p.Add(c => c.Termino, "nunez"));
        Pintado(cut).Should().Be("García [Núñez]");

        cut.Render(p => p.Add(c => c.Termino, string.Empty));
        cut.Markup.Should().Be("García Núñez");

        cut.Render(p => p.Add(c => c.Texto, "Nunez Garcia").Add(c => c.Termino, "nunez"));
        Pintado(cut).Should().Be("[Nunez] Garcia");
    }

    /// <summary>
    /// Un sustituto suelto en el buscador (un emoji a medio pegar) no es Unicode válido. La fila
    /// se pinta igual, sin marca: el resaltado nunca tira el render.
    /// </summary>
    [Fact]
    public void Un_termino_que_no_es_unicode_valido_no_rompe_el_render()
    {
        var suelto = new string((char)0xD83C, 1);

        var cut = Pintar("García Núñez", suelto);

        cut.Markup.Should().Be("García Núñez");
    }
}
