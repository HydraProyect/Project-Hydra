using System.Text.RegularExpressions;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Un nombre de icono que <c>Icono</c> no conoce cae en la rama <c>_</c> del
/// <c>switch</c> y pinta un <c>&lt;g&gt;&lt;/g&gt;</c> vacío: el render da verde y no
/// dibuja nada. Estos tests fijan que cada nombre registrado en el catálogo
/// produce geometría real, y que el icono 360 (P41a) está dentro.
/// </summary>
public class IconoCatalogoTests : BunitContext
{
    private const string FormasSvg = "path, line, circle, rect, polyline, polygon, ellipse";

    // El nombre del icono es la clave de cada brazo del switch de Icono.razor:
    // ocho espacios de sangría, el nombre entre comillas y "=>".
    private static readonly Regex BrazoDelCatalogo = new(
        "^ {8}\"(?<nombre>[^\"]+)\" =>", RegexOptions.Multiline);

    [Fact]
    public void El_icono_360_esta_en_el_catalogo_y_dibuja_los_dos_arcos_y_el_punto()
    {
        var cut = Render<Icono>(p => p.Add(x => x.Nombre, "360"));

        cut.FindAll("svg path").Should().HaveCount(2, "son dos arcos abiertos enfrentados");
        cut.FindAll("svg circle").Should().HaveCount(1, "y un punto central");
        cut.Find("svg").GetAttribute("aria-hidden").Should().Be("true");

        var arcos = cut.FindAll("svg path").Select(a => a.GetAttribute("d")).ToArray();
        arcos.Should().BeEquivalentTo(
            "M8.5 4.8A8.2 8.2 0 0 0 8.5 19.2",
            "M15.5 4.8A8.2 8.2 0 0 1 15.5 19.2");
    }

    [Fact]
    public void El_control_positivo_un_nombre_desconocido_da_svg_vacio()
    {
        // Sin esto, "todos los iconos dibujan algo" podría ser cierto solo porque
        // el detector de vacíos no distingue un icono vacío de uno bueno.
        var cut = Render<Icono>(p => p.Add(x => x.Nombre, "esto-no-existe"));

        cut.FindAll("svg").Should().HaveCount(1);
        cut.FindAll(FormasSvg).Should().BeEmpty();
    }

    [Fact]
    public void Todos_los_nombres_del_catalogo_dibujan_alguna_forma()
    {
        var nombres = NombresDelCatalogo();

        // Si el patrón deja de casar con el fichero, la lista se queda corta y el
        // test seguiría verde sin comprobar casi nada.
        nombres.Should().Contain(["dashboard", "clientes", "tendencia", "360"]);
        nombres.Count.Should().BeGreaterThan(40, "el catálogo real tiene decenas de iconos");

        var vacios = nombres
            .Where(n => Render<Icono>(p => p.Add(x => x.Nombre, n)).FindAll(FormasSvg).Count == 0)
            .ToList();

        vacios.Should().BeEmpty("un icono registrado sin geometría es un hueco que da verde y no dibuja");
    }

    private static List<string> NombresDelCatalogo()
    {
        var ruta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Components", "DesignSystem", "Icono.razor");
        File.Exists(ruta).Should().BeTrue("Icono.razor debería existir — si se movió, actualiza este test");

        return BrazoDelCatalogo.Matches(File.ReadAllText(ruta))
            .Select(m => m.Groups["nombre"].Value)
            .Distinct()
            .ToList();
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
