using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cubre el primer consumidor del componente "Wizard/Stepper" que el Design
/// System no tenía todavía (Fase A1 — el asistente de alta guiada Cliente →
/// Empresa → Centro). Solo prueba el propio indicador: qué se pinta y qué
/// dispara — el orquestado real (guardado incremental por paso) lo cubren
/// los tests de los Commands que la página ya reutiliza sin cambios.
/// </summary>
public class IndicadorPasosTests : BunitContext
{
    private static readonly IReadOnlyList<PasoDefinicion> TresPasos =
    [
        new("cliente", "Cliente"),
        new("empresa", "Empresa"),
        new("centro", "Centro")
    ];

    [Fact]
    public void Pinta_un_elemento_por_cada_paso_con_su_titulo()
    {
        var cut = Render<IndicadorPasos>(parametros => parametros
            .Add(p => p.Pasos, TresPasos)
            .Add(p => p.PasoActual, "cliente"));

        var items = cut.FindAll(".indicador-pasos-item");

        items.Should().HaveCount(3);
        cut.Markup.Should().Contain("Cliente").And.Contain("Empresa").And.Contain("Centro");
    }

    [Fact]
    public void El_paso_actual_lleva_aria_current_y_la_clase_activa()
    {
        var cut = Render<IndicadorPasos>(parametros => parametros
            .Add(p => p.Pasos, TresPasos)
            .Add(p => p.PasoActual, "empresa"));

        var items = cut.FindAll(".indicador-pasos-item");

        items[1].ClassList.Should().Contain("indicador-pasos-activo");
        items[1].QuerySelector("[aria-current='step']").Should().NotBeNull();
        items[0].ClassList.Should().NotContain("indicador-pasos-activo");
    }

    [Fact]
    public void Un_paso_completado_muestra_el_check_en_vez_del_numero()
    {
        var cut = Render<IndicadorPasos>(parametros => parametros
            .Add(p => p.Pasos, TresPasos)
            .Add(p => p.PasoActual, "empresa")
            .Add(p => p.PasosCompletados, new HashSet<string> { "cliente" }));

        var items = cut.FindAll(".indicador-pasos-item");

        items[0].ClassList.Should().Contain("indicador-pasos-completado");
        // El paso completado pinta el icono "check" del catálogo, no el glifo "✓":
        // se comprueba el svg, porque el texto del círculo ya está vacío.
        var circuloCompletado = items[0].QuerySelector(".indicador-pasos-circulo")!;
        circuloCompletado.QuerySelector("svg.icono").Should().NotBeNull();
        circuloCompletado.TextContent.Trim().Should().BeEmpty();
        // El paso 3 (índice 2), ni completado ni activo, sigue mostrando su número.
        items[2].QuerySelector(".indicador-pasos-circulo")!.TextContent.Should().Be("3");
    }

    [Fact]
    public void Sin_PermitirVolver_un_paso_completado_no_es_clicable()
    {
        var cut = Render<IndicadorPasos>(parametros => parametros
            .Add(p => p.Pasos, TresPasos)
            .Add(p => p.PasoActual, "empresa")
            .Add(p => p.PasosCompletados, new HashSet<string> { "cliente" })
            .Add(p => p.PermitirVolver, false));

        cut.FindAll(".indicador-pasos-item")[0].QuerySelector("button").Should().BeNull();
    }

    [Fact]
    public void Con_PermitirVolver_un_paso_completado_dispara_OnPasoSeleccionado_al_clicar()
    {
        string? seleccionado = null;

        var cut = Render<IndicadorPasos>(parametros => parametros
            .Add(p => p.Pasos, TresPasos)
            .Add(p => p.PasoActual, "empresa")
            .Add(p => p.PasosCompletados, new HashSet<string> { "cliente" })
            .Add(p => p.PermitirVolver, true)
            .Add(p => p.OnPasoSeleccionado, v => seleccionado = v));

        cut.FindAll(".indicador-pasos-item")[0].QuerySelector("button")!.Click();

        seleccionado.Should().Be("cliente");
    }

    [Fact]
    public void Con_PermitirVolver_el_paso_activo_no_es_clicable_aunque_este_completado()
    {
        // Un paso no se selecciona a sí mismo — evita un ciclo sin sentido de
        // "volver" al paso en el que ya se está.
        var cut = Render<IndicadorPasos>(parametros => parametros
            .Add(p => p.Pasos, TresPasos)
            .Add(p => p.PasoActual, "cliente")
            .Add(p => p.PasosCompletados, new HashSet<string> { "cliente" })
            .Add(p => p.PermitirVolver, true));

        cut.FindAll(".indicador-pasos-item")[0].QuerySelector("button").Should().BeNull();
    }
}
