using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

internal static class ComprobacionPieListado
{
    public static void Pagina<T>(IRenderedComponent<T> cut, int pagina, int totalPaginas) where T : class, IComponent
    {
        var paginador = cut.FindComponent<PaginadorSimple>().Instance;
        paginador.PaginaActual.Should().Be(pagina);
        paginador.TotalPaginas.Should().Be(totalPaginas);
    }

    public static void Vacio<T>(IRenderedComponent<T> cut) where T : IComponent
    {
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".tarjeta-listado").Should().ContainSingle();
            cut.Find(".tarjeta-listado-pie .paginador-texto").TextContent.Should().Be("Mostrando 0 de 0");
            cut.FindAll(".tarjeta-listado-pie .paginador-opciones-tamano button")
                .Select(b => b.TextContent).Should().Equal("20", "50", "100");
            cut.FindAll(".paginador-inicio button").Should().BeEmpty();
            cut.FindAll(".paginador-tamano-select").Should().BeEmpty();
            cut.FindAll(".paginador-centro .leyenda-atajos-lista").Should().ContainSingle();
        });
    }
}

public partial class CentrosListaPatronTests
{
    [Fact]
    public void El_pie_del_listado_vacio_sigue_dentro_de_la_tarjeta() =>
        ComprobacionPieListado.Vacio(Renderizar(ConCentros()));
}

public partial class EmpresasListaGen2Tests
{
    [Fact]
    public void El_pie_del_listado_vacio_sigue_dentro_de_la_tarjeta() =>
        ComprobacionPieListado.Vacio(Renderizar(new MediatorFalso()));
}

public partial class SubcontratasListaGen2Tests
{
    [Fact]
    public void El_pie_del_listado_vacio_sigue_dentro_de_la_tarjeta() =>
        ComprobacionPieListado.Vacio(Renderizar(new MediatorFalso()));
}

public partial class TrabajadoresListaGen2Tests
{
    [Fact]
    public void El_pie_del_listado_vacio_sigue_dentro_de_la_tarjeta() =>
        ComprobacionPieListado.Vacio(Renderizar(new MediatorFalso()));
}

public partial class ClientesListaGen2Tests
{
    [Fact]
    public void El_pie_del_listado_vacio_sigue_dentro_de_la_tarjeta() =>
        ComprobacionPieListado.Vacio(Renderizar(new MediatorFalso()));
}

public partial class DocumentosGen2Tests
{
    [Fact]
    public void El_pie_del_listado_vacio_sigue_dentro_de_la_tarjeta()
    {
        var (cut, _) = Renderizar();
        ComprobacionPieListado.Vacio(cut);
    }
}

public partial class VisitasGen2Tests
{
    [Fact]
    public void El_pie_del_listado_vacio_sigue_dentro_de_la_tarjeta() =>
        ComprobacionPieListado.Vacio(Renderizar(new MediatorVisitas()));

    [Fact]
    public async Task El_pie_no_inventa_un_cero_antes_de_recibir_el_total()
    {
        var mediador = new MediatorVisitas { DiferirLista = true };
        var cut = Renderizar(mediador);

        cut.FindAll(".paginador-texto").Should().BeEmpty();
        await cut.InvokeAsync(() => mediador.CargasPendientes.Single().Respuesta.SetResult(Pagina()));

        ComprobacionPieListado.Vacio(cut);
    }
}

public partial class ProyectosGen2Tests
{
    [Fact]
    public async Task El_pie_del_listado_vacio_sigue_dentro_de_la_tarjeta() =>
        ComprobacionPieListado.Vacio(await RenderizarConClienteAsync());
}
