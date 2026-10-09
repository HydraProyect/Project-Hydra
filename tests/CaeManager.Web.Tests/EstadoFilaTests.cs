using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// Celda de estado de un listado (Listados 2/7): la pastilla del estado y, debajo, el motivo en una línea. Lo
/// que pide acción lleva pastilla de color; lo correcto NO: punto verde y texto gris, para que el color quede
/// reservado a lo que hay que atender.
/// </summary>
public class EstadoFilaTests : BunitContext
{
    [Fact]
    public void Lo_correcto_no_lleva_pastilla_sino_punto_y_texto()
    {
        var cut = Render<EstadoFila>(p => p
            .Add(c => c.Correcto, true)
            .Add(c => c.Texto, "Sin incidencias")
            // El tono se ignora si es correcto: ni siquiera un tono de peligro pinta pastilla.
            .Add(c => c.Tono, TonoBadge.Peligro));

        cut.FindAll(".badge").Should().BeEmpty();
        var correcto = cut.Find("[data-pieza=estado-correcto]");
        correcto.ClassList.Should().Contain("estado-fila-correcto");
        correcto.TextContent.Trim().Should().Be("Sin incidencias");
        var punto = correcto.QuerySelector(".estado-fila-punto")!;
        punto.GetAttribute("aria-hidden").Should().Be("true", "el punto es decoración: el estado lo dice el texto");
    }

    [Theory]
    [InlineData(TonoBadge.Peligro, "badge-peligro")]
    [InlineData(TonoBadge.Advertencia, "badge-advertencia")]
    [InlineData(TonoBadge.Tolerancia, "badge-tolerancia")]
    [InlineData(TonoBadge.Neutro, "badge-neutro")]
    public void Lo_que_pide_accion_lleva_pastilla_con_la_clase_de_su_tono(TonoBadge tono, string claseEsperada)
    {
        var cut = Render<EstadoFila>(p => p
            .Add(c => c.Tono, tono)
            .Add(c => c.Texto, "Vencido"));

        var pastilla = cut.Find(".estado-fila .badge");
        pastilla.ClassList.Should().Contain(claseEsperada);
        pastilla.TextContent.Trim().Should().Be("Vencido");
        cut.FindAll("[data-pieza=estado-correcto]").Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_motivo_no_se_pinta_la_linea_del_motivo(string? motivo)
    {
        var cut = Render<EstadoFila>(p => p
            .Add(c => c.Tono, TonoBadge.Peligro)
            .Add(c => c.Texto, "Vencido")
            .Add(c => c.Motivo, motivo));

        cut.FindAll(".estado-fila-motivo").Should().BeEmpty();
        cut.Find(".estado-fila").TextContent.Trim().Should().Be("Vencido");
    }

    [Fact]
    public void El_motivo_va_debajo_de_la_pastilla_cuando_se_pasa()
    {
        var cut = Render<EstadoFila>(p => p
            .Add(c => c.Tono, TonoBadge.Peligro)
            .Add(c => c.Texto, "Vencido")
            .Add(c => c.Motivo, "Hace 12 días"));

        cut.Find(".estado-fila-motivo").TextContent.Trim().Should().Be("Hace 12 días");
        cut.Find(".estado-fila").Children.Select(h => h.ClassName)
            .Should().HaveCount(2).And.HaveElementAt(1, "estado-fila-motivo", "el motivo va después de la pastilla");
    }

    [Fact]
    public void Lo_correcto_tambien_puede_llevar_motivo()
    {
        var cut = Render<EstadoFila>(p => p
            .Add(c => c.Correcto, true)
            .Add(c => c.Texto, "Vigente")
            .Add(c => c.Motivo, "Sin caducidad"));

        cut.FindAll(".badge").Should().BeEmpty();
        cut.Find(".estado-fila-motivo").TextContent.Trim().Should().Be("Sin caducidad");
    }

    [Fact]
    public void El_motivo_con_contenido_propio_gana_al_motivo_de_texto()
    {
        var cut = Render<EstadoFila>(p => p
            .Add(c => c.Tono, TonoBadge.Advertencia)
            .Add(c => c.Texto, "Por vencer")
            .Add(c => c.Motivo, "texto simple")
            .Add(c => c.MotivoContenido, (RenderFragment)(b => b.AddMarkupContent(0, "<a href=\"/x\">2 documentos</a>"))));

        var motivo = cut.FindAll(".estado-fila-motivo").Should().ContainSingle().Subject;
        motivo.QuerySelector("a")!.TextContent.Should().Be("2 documentos");
        motivo.TextContent.Should().NotContain("texto simple");
    }

    [Fact]
    public void El_titulo_explica_el_estado_tanto_en_la_pastilla_como_en_lo_correcto()
    {
        var conPastilla = Render<EstadoFila>(p => p
            .Add(c => c.Tono, TonoBadge.Peligro)
            .Add(c => c.Texto, "Vencido")
            .Add(c => c.Titulo, "Peor estado entre sus alertas"));
        var correcto = Render<EstadoFila>(p => p
            .Add(c => c.Correcto, true)
            .Add(c => c.Texto, "Sin incidencias")
            .Add(c => c.Titulo, "No tiene alertas abiertas"));

        conPastilla.Find(".badge").GetAttribute("title").Should().Be("Peor estado entre sus alertas");
        correcto.Find("[data-pieza=estado-correcto]").GetAttribute("title").Should().Be("No tiene alertas abiertas");
    }
}
