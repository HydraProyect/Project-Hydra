using Bunit;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La tabla de Visitas con las columnas de la maqueta: «Visita» (el Centro y, debajo, el titular del
/// Centro y la Empresa) · Fechas · Trab. · Origen · Documentación · Avisada · Estado. La urgencia deja de
/// ser una columna propia: es el estado de la fila, con el tramo de antelación como motivo, y lo que no
/// pide acción («Normal») no lleva pastilla de color.
/// </summary>
public partial class VisitasGen2Tests
{
    [Fact]
    public void Las_columnas_son_las_de_la_maqueta_y_en_su_orden()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte") } };
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Norte"));

        cut.FindAll("thead th").Select(th => th.TextContent.Trim())
            .Should().Equal("Visita", "Fechas", "Trab.", "Origen", "Documentación", "Avisada", "Estado", "");
    }

    [Fact]
    public void La_columna_Visita_lleva_el_Centro_y_debajo_el_titular_y_la_Empresa_cada_uno_con_su_boton()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte") } };
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Norte"));

        var celda = Fila(cut, "Centro Norte").QuerySelector("td")!;
        celda.QuerySelectorAll("button.enlace-nombre-fila").Select(b => b.TextContent.Trim())
            .Should().Equal("Centro Norte", "Iberojet S.A.", "Instalaciones Arbeko S.L.");
        celda.QuerySelectorAll(".visitas-celda-titular-empresa button").Select(b => b.GetAttribute("title"))
            .Should().Equal("Titular del centro", "Empresa");
    }

    [Theory]
    [InlineData(NivelUrgenciaVisita.Critica, "Crítica", true)]
    [InlineData(NivelUrgenciaVisita.Urgente, "Urgente", true)]
    [InlineData(NivelUrgenciaVisita.EnCurso, "En curso", true)]
    [InlineData(NivelUrgenciaVisita.Normal, "Normal", false)]
    public void La_urgencia_es_el_estado_de_la_fila_y_solo_lo_que_pide_atencion_lleva_pastilla(
        NivelUrgenciaVisita nivel, string rotulo, bool conPastilla)
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte", urgencia: nivel) } };
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Norte"));

        var estado = Fila(cut, "Centro Norte").QuerySelector("td.col-estado .estado-fila")!;
        estado.TextContent.Trim().Should().Be(rotulo);
        estado.QuerySelectorAll(".badge").Should().HaveCount(conPastilla ? 1 : 0);
        estado.QuerySelectorAll("[data-pieza=estado-correcto]").Should().HaveCount(conPastilla ? 0 : 1);
    }

    [Fact]
    public async Task Una_cancelada_lo_dice_en_Estado_con_su_motivo_y_deja_Documentacion_en_blanco()
    {
        var cancelada = Visita("Planta Zaragoza", urgencia: NivelUrgenciaVisita.Critica) with { EstaCancelada = true, MotivoCancelacion = "Obra aplazada" };
        var mediator = new MediatorVisitas { Visitas = { cancelada } };
        var cut = Renderizar(mediator);
        await FiltrosVisitasDePrueba.ElegirAsync(cut, "Solo activas", "No");

        var fila = Fila(cut, "Planta Zaragoza");
        var estado = fila.QuerySelector("td.col-estado .estado-fila")!;
        estado.QuerySelector(".badge")!.TextContent.Trim().Should().Be("Cancelada", "no se pinta la urgencia de una visita que ya no va a ocurrir");
        var motivo = estado.QuerySelector(".estado-fila-motivo [data-pieza=motivo-cancelacion]")!;
        motivo.TextContent.Trim().Should().Be("Obra aplazada");
        motivo.GetAttribute("title").Should().Be("Obra aplazada", "el motivo se recorta a una línea: el title lo da entero");
        fila.QuerySelector("[data-pieza=sin-documentacion]")!.TextContent.Trim().Should().Be("—");
        fila.TextContent.Should().NotContain("Por gestionar").And.NotContain("Gestionada");
        fila.QuerySelectorAll(".badge").Should().ContainSingle("«Cancelada» no se repite en la columna «Documentación»");
    }
}
