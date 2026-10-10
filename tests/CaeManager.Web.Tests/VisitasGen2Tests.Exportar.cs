using Bunit;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El «⋯» de la cabecera de Visitas: las dos entradas de exportación (decisión D2 del
/// 2026-10-08). «Esta vista» lleva los criterios de la lista —incluida la vista de activas,
/// que es la de por defecto—; «todo» va sin criterios y por eso incluye el historial.
/// </summary>
public partial class VisitasGen2Tests
{
    [Fact]
    public void El_menu_de_cabecera_ofrece_exportar_esta_vista_con_sus_criterios_y_exportar_todo()
    {
        var mediator = new MediatorVisitas { Visitas = { Visita("Centro Norte"), Visita("Planta Zaragoza", notificado: true) } };
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Norte"));

        cut.Find("header.cabecera-pagina .menu-acciones-disparador").Click();

        cut.FindAll("header.cabecera-pagina .menu-acciones-item").Select(i => i.TextContent.Trim())
            .Should().Equal("Exportar esta vista (filas: 2)", "Exportar todo");
        var enlaces = cut.FindAll("header.cabecera-pagina a.menu-acciones-item").Select(i => i.GetAttribute("href")).ToList();
        enlaces[0].Should().StartWith("/visitas/exportar.xlsx?").And.Contain("activas=true");
        enlaces[1].Should().Be("/visitas/exportar.xlsx");
    }
}
