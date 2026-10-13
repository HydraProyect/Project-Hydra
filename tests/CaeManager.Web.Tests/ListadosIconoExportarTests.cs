using Microsoft.AspNetCore.WebUtilities;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

internal static class ComprobacionIconoExportar
{
    internal static void Criterios<T>(IRenderedComponent<T> cut, string ruta,
        Type endpoint, IReadOnlyDictionary<string, string> esperados) where T : class, IComponent
    {
        cut.WaitForAssertion(() =>
        {
            var href = cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href")!;
            var uri = new Uri(new Uri("https://prueba.invalid"), href);
            uri.AbsolutePath.Should().Be(ruta);
            var criterios = QueryHelpers.ParseQuery(uri.Query);
            criterios.Keys.Should().BeEquivalentTo(esperados.Keys, "el endpoint recibe exactamente los criterios activos, sin paginación");
            foreach (var (nombre, valor) in esperados)
                criterios[nombre].ToString().Should().Be(valor);
            criterios.Values.Where(v => string.IsNullOrWhiteSpace(v.ToString())).Should().BeEmpty();
            var parametros = endpoint.GetMethod("ExportarAsync")!.GetParameters().Select(p => p.Name);
            foreach (var nombre in criterios.Keys)
                parametros.Should().Contain(nombre, "el nombre debe coincidir con el parámetro real del endpoint");
        });
    }

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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void El_icono_exporta_criterios_reales_sin_parametros_vacios(bool activos)
    {
        _consultaExtra = activos ? $"empresa={EmpresaId}&orden=matricula-desc" : "empresa=&subcontrata=&orden=";
        var cut = Renderizar(busqueda: activos ? "Peña & Hijos" : null, estado: activos ? "Vencido" : null);
        var esperados = activos
            ? new Dictionary<string, string> { ["q"] = "Peña & Hijos", ["estado"] = "Vencido", ["empresa"] = EmpresaId.ToString(), ["orden"] = "NumeroPlaca", ["desc"] = "true" }
            : new Dictionary<string, string>();
        ComprobacionIconoExportar.Criterios(cut, "/vehiculos/exportar.xlsx",
            typeof(CaeManager.Web.Features.Vehiculos.VehiculosEndpoints), esperados);
    }

    [Fact]
    public void La_cabecera_adopta_exportacion_filtrada_con_menu_solo_Exportar_todo() =>
        ComprobacionIconoExportar.Cabecera(Renderizar(), "/vehiculos/exportar.xlsx", false);
}

public partial class ProyectosGen2Tests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_icono_exporta_criterios_reales_sin_parametros_vacios(bool activos)
    {
        var cut = await RenderizarConClienteAsync(activos
            ? "proyectos?q=Pe%C3%B1a%20%26%20Hijos&estado=abiertos"
            : "proyectos?q=&estado=");
        var esperados = new Dictionary<string, string> { ["cliente"] = ClienteId.ToString() };
        if (activos)
        {
            esperados["q"] = "Peña & Hijos";
            esperados["estado"] = "abiertos";
        }
        ComprobacionIconoExportar.Criterios(cut, "/proyectos/exportar.xlsx",
            typeof(CaeManager.Web.Features.Proyectos.ProyectosEndpoints), esperados);
    }

    [Fact]
    public async Task La_cabecera_adopta_exportacion_filtrada_con_menu_solo_Exportar_todo() =>
        ComprobacionIconoExportar.Cabecera(await RenderizarConClienteAsync(), "/proyectos/exportar.xlsx", false);
}

public partial class VisitasGen2Tests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void El_icono_exporta_criterios_reales_sin_parametros_vacios(bool activos)
    {
        var cut = Renderizar(new MediatorVisitas(), url: activos
            ? "visitas?q=Pe%C3%B1a%20%26%20Hijos&estado=PorGestionar&activas=true&urgentes=true&notificado=no&orden=documentacion"
            : "visitas?q=&estado=&activas=false&urgentes=false&notificado=&orden=");
        var esperados = activos
            ? new Dictionary<string, string> { ["q"] = "Peña & Hijos", ["estado"] = "PorGestionar", ["activas"] = "true", ["urgentes"] = "true", ["notificado"] = "no", ["orden"] = "PorGestionar", ["desc"] = "true" }
            : new Dictionary<string, string>();
        ComprobacionIconoExportar.Criterios(cut, "/visitas/exportar.xlsx",
            typeof(CaeManager.Web.Features.Visitas.VisitasEndpoints), esperados);
    }

    [Fact]
    public void La_cabecera_adopta_exportacion_filtrada_con_menu_solo_Exportar_todo() =>
        ComprobacionIconoExportar.Cabecera(Renderizar(new MediatorVisitas()), "/visitas/exportar.xlsx", false);
}

public partial class GestionesListaGen2Tests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void El_icono_exporta_criterios_reales_sin_parametros_vacios(bool activos)
    {
        var cut = Renderizar(new MediatorFalso(), url: activos
            ? "gestiones?q=Pe%C3%B1a%20%26%20Hijos&estado=Pendiente&orden=creada-desc"
            : "gestiones?q=&estado=&orden=");
        var esperados = activos
            ? new Dictionary<string, string> { ["q"] = "Peña & Hijos", ["estado"] = "Pendiente", ["orden"] = "CreadoEnUtc", ["desc"] = "true" }
            : new Dictionary<string, string> { ["orden"] = "Estado" };
        ComprobacionIconoExportar.Criterios(cut, "/gestiones/exportar.xlsx",
            typeof(CaeManager.Web.Features.Gestiones.GestionesEndpoints), esperados);
    }

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
