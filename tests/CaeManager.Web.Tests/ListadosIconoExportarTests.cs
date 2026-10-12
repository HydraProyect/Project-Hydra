using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

internal static class ComprobacionIconoExportar
{
    internal static void Cabecera<T>(IRenderedComponent<T> cut, string ruta, bool conOtrasAcciones) where T : class, IComponent
    {
        var enlace = cut.FindAll("header.cabecera-pagina a[data-keytip=X]").Should().ContainSingle().Which;
        enlace.GetAttribute("href").Should().StartWith(ruta);
        enlace.GetAttribute("aria-label").Should().Be("Exportar a Excel");
        enlace.HasAttribute("download").Should().BeTrue();
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().ContainSingle();
        cut.FindAll("header.cabecera-pagina .cabecera-listado-icono")
            .Select(e => e.GetAttribute("data-keytip")).Where(k => k != "S").Should().Equal("X", "K");
        var menu = cut.Find("header.cabecera-pagina .menu-acciones-disparador");
        menu.Click();
        var exportaciones = cut.FindAll($"header.cabecera-pagina a.menu-acciones-item[href^='{ruta}']");
        exportaciones.Should().ContainSingle().Which.GetAttribute("href").Should().Be(ruta);
        var entradas = cut.FindAll("header.cabecera-pagina .menu-acciones-item");
        entradas.Select(e => e.TextContent.Trim()).Should().Contain("Exportar todo")
            .And.NotContain(e => e.StartsWith("Exportar esta vista", StringComparison.Ordinal));
        if (!conOtrasAcciones)
            entradas.Select(e => e.TextContent.Trim()).Should().Equal("Exportar todo");
        else
            entradas.Count.Should().BeGreaterThan(1, "se conservan las acciones secundarias de la maqueta");
        menu.Click();
    }
}

public partial class TrabajadoresListaGen2Tests
{
    [Fact]
    public void La_cabecera_adopta_exportacion_filtrada_con_menu_solo_Exportar_todo() =>
        ComprobacionIconoExportar.Cabecera(Renderizar(new MediatorFalso()), "/trabajadores/exportar.xlsx", false);
}

public partial class EmpresasListaGen2Tests
{
    [Fact]
    public void La_cabecera_adopta_exportacion_filtrada_con_menu_solo_Exportar_todo() =>
        ComprobacionIconoExportar.Cabecera(Renderizar(new MediatorFalso()), "/empresas/exportar.xlsx", false);
}

public partial class SubcontratasListaGen2Tests
{
    [Fact]
    public void La_cabecera_adopta_exportacion_filtrada_con_menu_solo_Exportar_todo() =>
        ComprobacionIconoExportar.Cabecera(Renderizar(new MediatorFalso()), "/subcontratas/exportar.xlsx", false);
}

public partial class VehiculosVacioPorFiltroTests
{
    [Fact]
    public void La_cabecera_adopta_exportacion_filtrada_con_menu_solo_Exportar_todo() =>
        ComprobacionIconoExportar.Cabecera(Renderizar(), "/vehiculos/exportar.xlsx", false);
}

public partial class ProyectosGen2Tests
{
    [Fact]
    public async Task La_cabecera_adopta_exportacion_filtrada_con_menu_solo_Exportar_todo() =>
        ComprobacionIconoExportar.Cabecera(await RenderizarConClienteAsync(), "/proyectos/exportar.xlsx", false);
}

public partial class VisitasGen2Tests
{
    [Fact]
    public void La_cabecera_adopta_exportacion_filtrada_con_menu_solo_Exportar_todo() =>
        ComprobacionIconoExportar.Cabecera(Renderizar(new MediatorVisitas()), "/visitas/exportar.xlsx", false);
}

public partial class GestionesListaGen2Tests
{
    [Fact]
    public void La_cabecera_adopta_exportacion_filtrada_con_menu_solo_Exportar_todo() =>
        ComprobacionIconoExportar.Cabecera(Renderizar(new MediatorFalso()), "/gestiones/exportar.xlsx", false);
}

public partial class ClientesListaGen2Tests
{
    [Fact]
    public void La_cabecera_adopta_exportacion_filtrada_y_conserva_Mas_acciones() =>
        ComprobacionIconoExportar.Cabecera(Renderizar(new MediatorFalso()), "/clientes/exportar.xlsx", true);
}

public partial class DocumentosGen2Tests
{
    [Fact]
    public void La_cabecera_adopta_exportacion_filtrada_y_conserva_Mas_acciones() =>
        ComprobacionIconoExportar.Cabecera(Renderizar().Cut, "/documentos/exportar.xlsx", true);
}

public partial class CentrosListaPatronTests
{
    [Fact]
    public void La_cabecera_adopta_exportacion_filtrada_y_conserva_asignaciones()
    {
        var cut = Renderizar(ConDosClientes(), url: "centros?q=Planta");
        ComprobacionIconoExportar.Cabecera(cut, "/centros/exportar.xlsx", true);
        cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href").Should().Contain("q=Planta");
        cut.Find("header.cabecera-pagina .menu-acciones-disparador").Click();
        cut.Find("a[href='/asignaciones/exportar.xlsx']").TextContent.Trim().Should().Be("Exportar asignaciones");
    }
}
