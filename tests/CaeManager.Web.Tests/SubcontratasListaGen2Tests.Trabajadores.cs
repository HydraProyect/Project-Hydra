using Bunit;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class SubcontratasListaGen2Tests
{
    [Fact]
    public async Task La_identidad_muestra_el_recuento_y_la_ventana_abre_la_ficha_del_trabajador()
    {
        var id = Guid.NewGuid();
        var fila = Subcontrata("Empresa contraparte") with
        {
            Trabajadores = [new TrabajadorIdentidadSubcontrataDto(id, "Ana", "Vidal"), new(Guid.NewGuid(), "Luis", "Sanz")]
        };
        var cut = Renderizar(new MediatorFalso { Subcontratas = [fila] });
        var ventana = cut.FindComponents<VentanaContexto>().Single(v => v.Instance.Titulo == "Trabajadores");

        ventana.Find(".ventana-contexto-disparador").TextContent.Should().Be("2 trabajadores");
        ventana.FindAll(".ventana-contexto-panel button").Should().HaveCount(2);
        await ventana.Find(".ventana-contexto-panel button").ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"/trabajadores/{id}");
        cut.FindAll(".workspace-panel").Should().BeEmpty("el clic no debe abrir además la vista rápida de la Empresa subcontratista");
    }

    [Fact]
    public void Sin_trabajadores_el_recuento_no_abre_una_ventana_vacia()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Empresa contraparte")] });
        var ventana = cut.FindComponents<VentanaContexto>().Single(v => v.Instance.Titulo == "Trabajadores");

        ventana.Markup.Should().Contain("0 trabajadores");
        ventana.FindAll(".ventana-contexto-panel").Should().BeEmpty();
    }
}
