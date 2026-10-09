using System.Text.RegularExpressions;
using Bunit;
using CaeManager.Web.Features.AtajosGlobales;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// Adopción de <c>LeyendaAtajosLista</c> en los diez listados (Cierre listados, línea H). Lee el fuente,
/// como <see cref="CatalogoAtajosSincronizadoConJsTests"/>: lo que se vigila es que cada pantalla la monte
/// una sola vez y que la leyenda diga la verdad —que omita justo las teclas que esa pantalla no maneja—, y
/// eso está repartido entre el marcado y el <c>ManejarAtajoAsync</c> de cada página.
/// </summary>
public class LeyendaAtajosListaAdopcionTests
{
    public static TheoryData<string> Paginas =>
    [
        "Trabajadores", "Empresas", "Vehiculos", "Subcontratas", "Proyectos",
        "Gestiones", "Visitas", "Documentos", "Clientes", "Centros"
    ];

    [Theory]
    [MemberData(nameof(Paginas))]
    public void La_pagina_monta_la_leyenda_exactamente_una_vez(string pagina)
    {
        Regex.Count(Leer(pagina, ".razor"), @"<LeyendaAtajosLista\b").Should().Be(1,
            $"{pagina}.razor lleva la leyenda de atajos al pie de su lista, una sola vez");
    }

    /// <summary>
    /// En estas seis la rejilla sigue montada con la lista vacía, así que la leyenda cuelga del mismo recuento
    /// que el paginador. Sin <c>HayFilas</c> se quedaría sola al pie de una lista sin filas que recorrer.
    /// </summary>
    [Theory]
    [InlineData("Trabajadores")]
    [InlineData("Clientes")]
    [InlineData("Vehiculos")]
    [InlineData("Documentos")]
    [InlineData("Gestiones")]
    [InlineData("Visitas")]
    public void Con_la_rejilla_siempre_montada_la_leyenda_depende_de_que_haya_filas(string pagina)
    {
        var linea = Leer(pagina, ".razor").Split('\n').Single(l => l.Contains("<LeyendaAtajosLista", StringComparison.Ordinal));

        linea.Should().Contain("HayFilas=\"@(_totalElementos > 0)\"",
            $"{pagina}.razor no desmonta la rejilla al quedarse sin resultados: la leyenda se esconde con HayFilas");
    }

    /// <summary>
    /// Una leyenda que anuncia una tecla que la pantalla no maneja es peor que una que falta. Lo que la
    /// pantalla maneja se mide en su código, no se declara:
    /// <list type="bullet">
    /// <item><description><c>j</c>, <c>k</c>, <c>Enter</c>, <c>x</c> y <c>e</c>: un <c>case "…"</c> o un
    /// <c>tecla == "…"</c> dentro de <c>ManejarAtajoAsync</c>;</description></item>
    /// <item><description><c>f</c>: no pasa por C#. <c>atajos-lista.js</c> enfoca el campo
    /// <c>[data-filtro-pantalla]</c>, que solo pinta <c>BarraFiltros</c> en su modo de pastillas.</description></item>
    /// </list>
    /// Si esta prueba se pone en rojo porque una pantalla ha ganado o perdido una tecla, se corrige el
    /// <c>Omitir</c> de su <c>&lt;LeyendaAtajosLista&gt;</c>, no esta medición.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paginas))]
    public void La_leyenda_omite_exactamente_las_teclas_que_la_pagina_no_maneja(string pagina)
    {
        var razor = Leer(pagina, ".razor");
        var manejadas = TeclasQueManeja(pagina, razor);

        manejadas.Should().Contain(["j", "k", "Enter"],
            $"sin moverse ni abrir, {pagina} no tendría atajos de lista que anunciar (¿ha cambiado la forma de ManejarAtajoAsync?)");

        var noManejadas = CatalogoAtajos.Lista
            .Where(a => a.Tecla.Split(" / ").Any(tecla => !manejadas.Contains(tecla)))
            .Select(a => a.Tecla);

        TeclasOmitidas(pagina, razor).Should().BeEquivalentTo(noManejadas,
            $"la leyenda de {pagina} enseña solo lo que la pantalla maneja de verdad (maneja: {string.Join(", ", manejadas.Order())})");
    }

    private static HashSet<string> TeclasQueManeja(string pagina, string razor)
    {
        var codigo = Leer(pagina, ".razor.cs");
        var manejador = Regex.Match(codigo, @"Task ManejarAtajoAsync\(string tecla\)\r?\n    \{(?<cuerpo>.*?)\r?\n    \}\r?\n", RegexOptions.Singleline);
        manejador.Success.Should().BeTrue($"{pagina}.razor.cs debería tener un ManejarAtajoAsync(string tecla) — si cambió de forma, actualiza este test");

        var manejadas = Regex.Matches(manejador.Groups["cuerpo"].Value, @"case ""(?<t>[^""]+)""|tecla (?:==|is) ""(?<t>[^""]+)""")
            .Select(m => m.Groups["t"].Value)
            .ToHashSet();

        if (Regex.IsMatch(razor, @"<BarraFiltros\b") && Regex.IsMatch(razor, @"<Pastillas>|\bPastillas="))
        {
            manejadas.Add("f");
        }

        return manejadas;
    }

    private static List<string> TeclasOmitidas(string pagina, string razor)
    {
        // Que la monte, y una sola vez, lo dice La_pagina_monta_la_leyenda_exactamente_una_vez.
        var linea = razor.Split('\n').FirstOrDefault(l => l.Contains("<LeyendaAtajosLista", StringComparison.Ordinal));
        if (linea is null || !linea.Contains("Omitir", StringComparison.Ordinal))
        {
            return [];
        }

        var omitir = Regex.Match(linea, @"Omitir=""@\(\[(?<teclas>[^\]]*)\]\)""");
        omitir.Success.Should().BeTrue(
            $"el Omitir de {pagina}.razor debe ser una lista literal (Omitir=\"@([\"x\", \"e\"])\") para que este test pueda leerla");

        return Regex.Matches(omitir.Groups["teclas"].Value, @"""(?<t>[^""]+)""").Select(m => m.Groups["t"].Value).ToList();
    }

    private static string Leer(string pagina, string extension)
    {
        var ruta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Features", pagina, "Pages", pagina + extension);
        File.Exists(ruta).Should().BeTrue($"{pagina}{extension} debería existir — si se movió o renombró, actualiza este test");
        return File.ReadAllText(ruta);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}

/// <summary>Lectura de la leyenda de atajos ya pintada dentro de una página (pruebas bUnit de los listados).</summary>
internal static class LeyendaAtajosEnPagina
{
    public const string Selector = "[data-pieza=leyenda-atajos]";

    /// <summary>Las teclas que enseña la única leyenda de la página, en orden. Falla si no hay exactamente una.</summary>
    public static List<string> TeclasDeLaLeyenda<TPagina>(this IRenderedComponent<TPagina> cut) where TPagina : IComponent =>
        cut.FindAll(Selector).Should().ContainSingle("la página lleva una sola leyenda de atajos al pie de la lista")
            .Subject.QuerySelectorAll("kbd").Select(k => k.TextContent).ToList();
}
