using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Franja de estado de un listado (Listados 2/7): una fila de botones, uno por estado, con selección múltiple.
/// Sustituye a la pastilla «Estado» / «Documentación» y a su chip. Aquí se prueba lo propio del componente: qué
/// emite cada clic, qué botón queda marcado (aria-pressed) y qué cifra enseña cada uno. Que cada listado mande
/// esa selección a su consulta y a su URL lo prueban los tests de cada pantalla.
/// </summary>
public class FranjaEstadoTests : BunitContext
{
    /// <summary>«Por vencer» agrupa dos estados de código, como en los listados reales.</summary>
    private static readonly IReadOnlyList<OpcionFranjaEstado> Opciones =
    [
        new("Vencidos", TonoBadge.Peligro, "Vencido"),
        new("Por vencer", TonoBadge.Advertencia, "Urgente", "Proximo"),
        new("Vigentes", TonoBadge.Exito, "Vigente")
    ];

    /// <summary>Lo que la franja ha emitido por <c>ValorChanged</c>, en orden; <c>null</c> es «sin selección».</summary>
    private readonly List<string?> _emitidos = [];

    public FranjaEstadoTests()
    {
        Services.AddLocalization();
    }

    private IRenderedComponent<FranjaEstado> Renderizar(
        string? valor = null, IReadOnlyDictionary<string, int>? recuentos = null, int? totalTodos = null,
        string? textoTodos = null, string? etiquetaAccesible = null) =>
        Render<FranjaEstado>(p => p
            .Add(c => c.Opciones, Opciones)
            .Add(c => c.Valor, valor)
            .Add(c => c.Recuentos, recuentos)
            .Add(c => c.TotalTodos, totalTodos)
            .Add(c => c.TextoTodos, textoTodos)
            .Add(c => c.EtiquetaAccesible, etiquetaAccesible)
            .Add(c => c.ValorChanged, EventCallback.Factory.Create<string?>(this, v => _emitidos.Add(v))));

    // ------------------------------------------------------------- Estructura

    [Fact]
    public void Es_un_grupo_con_nombre_accesible_y_Todos_va_delante_de_las_opciones()
    {
        var cut = Renderizar();

        var franja = cut.Find(".franja-estado");
        franja.GetAttribute("role").Should().Be("group");
        franja.GetAttribute("aria-label").Should().Be("Filtrar por estado");
        cut.RotulosDeFranja().Should().Equal("Todos", "Vencidos", "Por vencer", "Vigentes");
        cut.FindAll(".franja-estado button").Should().OnlyContain(b => b.GetAttribute("type") == "button");
        cut.FindAll(".franja-estado-punto").Select(p => p.ClassName).Should().Equal(
            "franja-estado-punto franja-estado-punto-peligro",
            "franja-estado-punto franja-estado-punto-advertencia",
            "franja-estado-punto franja-estado-punto-exito");
    }

    [Fact]
    public void El_texto_de_Todos_y_la_etiqueta_accesible_se_pueden_cambiar()
    {
        var cut = Renderizar(textoTodos: "Todas", etiquetaAccesible: "Filtrar por documentación");

        cut.RotulosDeFranja()[0].Should().Be("Todas");
        cut.Find(".franja-estado").GetAttribute("aria-label").Should().Be("Filtrar por documentación");
    }

    // -------------------------------------------------------------- Selección

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Sin_seleccion_el_marcado_es_Todos(string? valor)
    {
        var cut = Renderizar(valor);

        cut.MarcadosEnFranja().Should().Equal("Todos");
        cut.FindAll(".franja-estado button").Select(b => b.GetAttribute("aria-pressed"))
            .Should().Equal("true", "false", "false", "false");
    }

    [Fact]
    public void Con_seleccion_se_marcan_sus_botones_y_Todos_no()
    {
        var cut = Renderizar("Vencido,Vigente");

        cut.MarcadosEnFranja().Should().Equal("Vencidos", "Vigentes");
        cut.BotonDeFranja("Todos").GetAttribute("aria-pressed").Should().Be("false");
    }

    [Fact]
    public void Todos_borra_la_seleccion_emitiendo_null()
    {
        var cut = Renderizar("Vencido,Urgente,Proximo");

        cut.BotonDeFranja("Todos").Click();

        _emitidos.Should().Equal([null]);
    }

    [Fact]
    public void Todos_no_emite_nada_si_ya_no_hay_seleccion()
    {
        var cut = Renderizar();

        cut.BotonDeFranja("Todos").Click();

        _emitidos.Should().BeEmpty("pulsar «Todos» sin filtro puesto no es un cambio: avisar recargaría la lista para nada");
    }

    [Fact]
    public void Marcar_un_boton_anade_su_valor_a_la_seleccion_que_habia()
    {
        var cut = Renderizar("Vigente");

        cut.BotonDeFranja("Vencidos").Click();

        _emitidos.Should().Equal("Vigente,Vencido");
    }

    [Fact]
    public void Marcar_un_boton_agrupado_anade_todos_sus_valores()
    {
        var cut = Renderizar("Vencido");

        cut.BotonDeFranja("Por vencer").Click();

        _emitidos.Should().Equal("Vencido,Urgente,Proximo");
    }

    [Fact]
    public void Desmarcar_un_boton_quita_solo_sus_valores()
    {
        var cut = Renderizar("Vencido,Urgente,Proximo");

        cut.BotonDeFranja("Por vencer").Click();

        _emitidos.Should().Equal("Vencido");
    }

    [Fact]
    public void Desmarcar_el_unico_boton_marcado_emite_null()
    {
        var cut = Renderizar("Vencido");

        cut.BotonDeFranja("Vencidos").Click();

        _emitidos.Should().Equal([null]);
    }

    /// <summary>
    /// Un enlace anterior a la franja trae un solo estado (<c>?estado=Urgente</c>). «Por vencer» sale marcado
    /// con solo uno de sus dos valores, y un clic lo limpia entero: si añadiera el que falta, haría falta un
    /// segundo clic para desmarcar lo que ya se veía marcado.
    /// </summary>
    [Fact]
    public void Un_boton_agrupado_con_solo_uno_de_sus_valores_esta_marcado_y_desmarcarlo_los_quita_todos()
    {
        var cut = Renderizar("Vencido,Urgente");
        cut.MarcadosEnFranja().Should().Equal("Vencidos", "Por vencer");

        cut.BotonDeFranja("Por vencer").Click();

        _emitidos.Should().Equal("Vencido");
    }

    /// <summary>
    /// Lo que la URL traiga y ningún botón conozca no es de la franja: no marca nada y se conserva tal cual al
    /// alternar. Descartarlo es decisión de la página (que valida la selección), no del componente.
    /// </summary>
    [Fact]
    public void Los_valores_desconocidos_se_conservan_al_marcar_y_al_desmarcar()
    {
        var cut = Renderizar("Inventado,Vencido");
        cut.MarcadosEnFranja().Should().Equal("Vencidos");

        cut.BotonDeFranja("Vigentes").Click();
        cut.BotonDeFranja("Vencidos").Click();

        _emitidos.Should().Equal("Inventado,Vencido,Vigente", "Inventado");
    }

    [Fact]
    public void La_seleccion_marcada_sigue_al_valor_que_le_pasa_la_pagina()
    {
        var cut = Renderizar("Vencido");

        cut.Render(p => p.Add(c => c.Valor, "Urgente,Proximo"));

        cut.MarcadosEnFranja().Should().Equal("Por vencer");
    }

    // -------------------------------------------------------------- Recuentos

    [Fact]
    public void Sin_recuentos_no_hay_cifras_y_los_botones_filtran_igual()
    {
        var cut = Renderizar();

        cut.FindAll(".franja-estado-recuento").Should().BeEmpty();
        cut.FindAll(".franja-estado-boton-vacio").Should().BeEmpty("sin cifras no se sabe si un botón está vacío");

        cut.BotonDeFranja("Vencidos").Click();

        _emitidos.Should().Equal("Vencido");
    }

    [Fact]
    public void Cada_boton_dice_su_recuento_el_agrupado_la_suma_y_Todos_la_suma_de_todos()
    {
        var cut = Renderizar(recuentos: new Dictionary<string, int>
        {
            ["Vencido"] = 3,
            ["Urgente"] = 2,
            ["Proximo"] = 5,
            ["Vigente"] = 40
        });

        cut.BotonDeFranja("Vencidos").RecuentoDeFranja().Should().Be(3);
        cut.BotonDeFranja("Por vencer").RecuentoDeFranja().Should().Be(7, "Urgente y Próximo se cuentan juntos");
        cut.BotonDeFranja("Vigentes").RecuentoDeFranja().Should().Be(40);
        cut.BotonDeFranja("Todos").RecuentoDeFranja().Should().Be(50);
    }

    /// <summary>
    /// Donde una misma fila puede estar en Urgente y en Próximo a la vez (un Cliente empresarial con alertas de
    /// los dos), sumar las dos claves la contaría dos veces: si la consulta da la cifra del conjunto con la clave
    /// «Urgente,Proximo», esa es la que vale.
    /// </summary>
    [Fact]
    public void La_clave_conjunta_de_un_boton_agrupado_gana_a_la_suma_de_sus_valores()
    {
        var cut = Renderizar(recuentos: new Dictionary<string, int>
        {
            ["Vencido"] = 3,
            ["Urgente"] = 2,
            ["Proximo"] = 5,
            ["Urgente,Proximo"] = 6
        });

        cut.BotonDeFranja("Por vencer").RecuentoDeFranja().Should().Be(6);
    }

    [Fact]
    public void TotalTodos_gana_a_la_suma_de_los_recuentos()
    {
        var cut = Renderizar(
            recuentos: new Dictionary<string, int> { ["Vencido"] = 3, ["Urgente"] = 2, ["Proximo"] = 5, ["Vigente"] = 40 },
            totalTodos: 44);

        cut.BotonDeFranja("Todos").RecuentoDeFranja().Should().Be(44,
            "si una fila cuenta en varios estados, la suma no es el total: lo da la consulta");
    }

    [Fact]
    public void TotalTodos_se_ensena_aunque_no_haya_recuentos_por_estado()
    {
        var cut = Renderizar(totalTodos: 12);

        cut.BotonDeFranja("Todos").RecuentoDeFranja().Should().Be(12);
        cut.FindAll(".franja-estado-recuento").Should().ContainSingle("los demás botones siguen sin cifra");
    }

    [Fact]
    public void Un_recuento_cero_se_ensena_y_marca_el_boton_como_vacio()
    {
        var cut = Renderizar(recuentos: new Dictionary<string, int> { ["Vencido"] = 0, ["Urgente"] = 0, ["Proximo"] = 1 });

        var vencidos = cut.BotonDeFranja("Vencidos");
        vencidos.RecuentoDeFranja().Should().Be(0);
        vencidos.ClassList.Should().Contain("franja-estado-boton-vacio");
        cut.BotonDeFranja("Por vencer").ClassList.Should().NotContain("franja-estado-boton-vacio");
        cut.BotonDeFranja("Vigentes").RecuentoDeFranja().Should().Be(0, "un estado que la consulta no trae cuenta cero");
        cut.BotonDeFranja("Vigentes").ClassList.Should().Contain("franja-estado-boton-vacio");
        cut.BotonDeFranja("Todos").ClassList.Should().NotContain("franja-estado-boton-vacio", "«Todos» nunca se atenúa");
    }

    [Fact]
    public void Un_boton_vacio_sigue_pudiendose_marcar()
    {
        var cut = Renderizar(recuentos: new Dictionary<string, int> { ["Vencido"] = 0 });

        cut.BotonDeFranja("Vencidos").Click();

        _emitidos.Should().Equal("Vencido");
    }
}
