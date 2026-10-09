using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// KeyTips: los diez listados no declaran sus letras, las heredan de los componentes compartidos.
/// <c>CatalogoAtajosSincronizadoConJsTests</c> lee los <c>.razor</c> como texto y vigila qué
/// letras se declaran; aquí se fija que el atributo llega al elemento que <c>keytips.js</c>
/// busca en el DOM (<c>data-keytip</c> sobre el control pulsable, no sobre un envoltorio), que es
/// lo que un refactor del componente puede perder sin tocar la letra.
/// </summary>
public class KeyTipsDeclaradosEnComponentesTests : BunitContext
{
    private static readonly IReadOnlyList<OpcionEstado> Opciones =
        [new("Vencido", "Con vencidos"), new("Urgente", "Con urgentes")];

    public KeyTipsDeclaradosEnComponentesTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// La pastilla deduce su letra (atributo vacío) y la deduce del NOMBRE del filtro, no del
    /// texto del disparador, que cambia con el valor elegido («Estado: Con vencidos»): sin
    /// <c>data-keytip-nombre</c> la letra bailaría al filtrar.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Vencido")]
    public void La_pastilla_de_filtro_deduce_su_letra_del_nombre_del_filtro_tenga_o_no_valor(string valor)
    {
        var cut = Render<PastillaFiltro>(p => p
            .Add(c => c.Etiqueta, "Estado")
            .Add(c => c.Opciones, Opciones)
            .Add(c => c.Valor, valor));

        var disparador = cut.Find(".menu-acciones-disparador");
        disparador.HasAttribute("data-keytip").Should().BeTrue();
        disparador.GetAttribute("data-keytip").Should().BeEmpty("la letra de una pastilla se deduce, no se declara");
        disparador.GetAttribute("data-keytip-nombre").Should().Be("Estado");
    }

    [Fact]
    public void Un_menu_sin_Keytip_no_se_ofrece_a_KeyTips()
    {
        var cut = Render<MenuAcciones>(p => p.Add(m => m.Etiqueta, "Acciones"));

        cut.Find(".menu-acciones-disparador").HasAttribute("data-keytip").Should().BeFalse(
            "un menú de fila no entra en KeyTips: solo los que su anfitrión declara");
    }

    [Fact]
    public void Cada_pestana_se_ofrece_a_KeyTips_con_letra_deducida()
    {
        var cut = Render<Pestanas>(p => p
            .Add(c => c.Definiciones, new List<PestanaDefinicion> { new("listado", "Listado"), new("plantillas", "Plantillas") })
            .Add(c => c.PestanaActiva, "listado"));

        var pestanas = cut.FindAll("[role=tab]");
        pestanas.Should().HaveCount(2);
        pestanas.Should().OnlyContain(p => p.HasAttribute("data-keytip") && p.GetAttribute("data-keytip") == string.Empty);
    }

    [Fact]
    public void La_barra_de_herramientas_declara_S_y_E_sobre_sus_botones()
    {
        var cut = Render<BarraHerramientasLista>(p => p
            .Add(b => b.OnAlternarTodos, _ => { })
            .Add(b => b.SeleccionMultipleChanged, _ => { }));

        cut.FindAll("button[data-keytip='S']").Should().ContainSingle();
        cut.FindAll("button[data-keytip='E']").Should().ContainSingle();
    }

    [Fact]
    public void La_barra_de_pastillas_declara_F_en_el_filtro_de_la_pantalla_y_L_en_Mas_filtros()
    {
        var cut = Render<BarraFiltros>(p => p
            .Add(b => b.Pastillas, (RenderFragment)(_ => { }))
            .Add(b => b.MasFiltros, (RenderFragment)(_ => { })));

        cut.FindAll("[data-keytip='F']").Should().ContainSingle()
            .Which.HasAttribute("data-filtro-pantalla").Should().BeTrue("F va sobre el campo, que es lo que recibe el foco");
        cut.FindAll("[data-keytip='L']").Should().ContainSingle()
            .Which.GetAttribute("aria-haspopup").Should().Be("menu", "L baja al nivel del menú «Más filtros»");
    }

    [Fact]
    public void La_barra_clasica_declara_F_en_su_buscador_y_no_tiene_L()
    {
        var cut = Render<BarraFiltros>();

        cut.FindAll("input[data-keytip='F']").Should().ContainSingle();
        cut.FindAll("[data-keytip='L']").Should().BeEmpty();
    }
}
