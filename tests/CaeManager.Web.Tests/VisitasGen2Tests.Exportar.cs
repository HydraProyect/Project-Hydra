using Bunit;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El icono de exportación de Visitas lleva los criterios de la lista, incluida la vista de activas
/// que se muestra por defecto. El menú de cabecera ofrece únicamente «Exportar todo», sin criterios.
/// </summary>
public partial class VisitasGen2Tests
{
    [Fact]
    public void El_icono_exporta_esta_vista_con_sus_criterios_con_menu_Exportar_todo()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte"), Visita("Planta Zaragoza", notificado: true) } };
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Norte"));

        cut.Find(".franja-estado button[data-estado=PorGestionar]").Click();
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().ContainSingle();
        cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href")
            .Should().StartWith("/visitas/exportar.xlsx?").And.Contain("activas=true")
            .And.Contain("estado=PorGestionar", "la selección de la franja es un criterio más de la vista");
    }
}
