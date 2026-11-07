using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cubre el icono opcional de <see cref="PestanaDefinicion"/>, añadido para dar
/// sitio al icono "plantilla" cuando Plantillas dejó de tener botón propio en el
/// menú lateral (REC-062) y pasó a ser una pestaña de Documentos.
///
/// <para>
/// Lo que se protege aquí es que <b>la etiqueta sigue siendo texto</b>: los E2E y
/// los lectores de pantalla localizan la pestaña por su nombre, y un icono no
/// puede sustituirlo ni ensuciarlo. El icono es decorativo — <c>Icono</c> ya le
/// pone <c>aria-hidden</c>.
/// </para>
/// </summary>
public class PestanasTests : BunitContext
{
    private static readonly IReadOnlyList<PestanaDefinicion> ConIconos =
    [
        new("listado", "Listado", "documentos"),
        new("plantillas", "Plantillas", "plantilla")
    ];

    private static readonly IReadOnlyList<PestanaDefinicion> SinIconos =
    [
        new("listado", "Listado"),
        new("plantillas", "Plantillas")
    ];

    [Fact]
    public void Una_pestana_con_icono_pinta_su_svg_y_conserva_la_etiqueta_como_texto()
    {
        var cut = Render<Pestanas>(parametros => parametros
            .Add(p => p.Definiciones, ConIconos)
            .Add(p => p.PestanaActiva, "listado"));

        var botones = cut.FindAll(".pestanas-boton");

        botones.Should().HaveCount(2);
        botones[1].QuerySelector("svg.icono").Should().NotBeNull();
        // La etiqueta no se sustituye por el icono: sigue siendo el texto por el
        // que se localiza la pestaña.
        botones[1].TextContent.Trim().Should().Be("Plantillas");
    }

    [Fact]
    public void Sin_icono_declarado_la_pestana_no_pinta_ningun_svg()
    {
        var cut = Render<Pestanas>(parametros => parametros
            .Add(p => p.Definiciones, SinIconos)
            .Add(p => p.PestanaActiva, "listado"));

        var botones = cut.FindAll(".pestanas-boton");

        // Las 14 tiras que no declaran icono no deben cambiar de aspecto: el
        // parámetro es opcional justamente para no tocarlas.
        botones.Should().OnlyContain(b => b.QuerySelector("svg.icono") == null);
        botones[1].TextContent.Trim().Should().Be("Plantillas");
    }

    [Fact]
    public void El_icono_no_altera_la_semantica_ARIA_de_la_pestana()
    {
        var cut = Render<Pestanas>(parametros => parametros
            .Add(p => p.Definiciones, ConIconos)
            .Add(p => p.PestanaActiva, "plantillas"));

        var activa = cut.FindAll(".pestanas-boton")[1];

        activa.GetAttribute("role").Should().Be("tab");
        activa.GetAttribute("aria-selected").Should().Be("true");
        // El svg del icono es decorativo; el nombre accesible sale de la etiqueta.
        activa.QuerySelector("svg.icono")!.GetAttribute("aria-hidden").Should().Be("true");
    }

    [Fact]
    public void Una_pestana_Proximamente_lleva_la_insignia_separada_de_la_etiqueta_y_sigue_siendo_una_pestana_activa()
    {
        Services.AddLocalization();
        IReadOnlyList<PestanaDefinicion> definiciones =
        [
            new("listado", "Listado"),
            new("plataformas", "Plataformas") { Proximamente = true }
        ];

        var cut = Render<Pestanas>(parametros => parametros
            .Add(p => p.Definiciones, definiciones)
            .Add(p => p.PestanaActiva, "listado"));

        var pestana = cut.FindAll("[role=tab]").Single(t => t.TextContent.Contains("Plataformas"));
        System.Text.RegularExpressions.Regex.Replace(pestana.TextContent, @"\s+", " ").Trim()
            .Should().Be("Plataformas Próximamente", "el nombre accesible lleva un espacio entre la etiqueta y la insignia");
        pestana.QuerySelector("span.insignia-proximamente").Should().NotBeNull();
        pestana.HasAttribute("disabled").Should().BeFalse();
        pestana.GetAttribute("tabindex").Should().Be("-1", "es una pestaña más del grupo: entra con las flechas");
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Listado").QuerySelector(".insignia-proximamente")
            .Should().BeNull("sin Proximamente no se pinta insignia: ningún consumidor existente cambia");
    }
}
