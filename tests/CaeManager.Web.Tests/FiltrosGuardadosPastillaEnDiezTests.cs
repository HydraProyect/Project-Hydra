using Microsoft.AspNetCore.Components;
using Bunit;
using CaeManager.Application.Configuracion.Queries;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

internal static class ComprobacionPastillaGuardados
{
    internal static FiltroGuardadoDto Filtro() => new(Guid.NewGuid(), "Vista guardada de prueba", "{}", DateTime.UtcNow);

    internal static async Task ComprobarAsync<T>(IRenderedComponent<T> cut, bool conGuardados) where T : IComponent
    {
        cut.WaitForAssertion(() => cut.FindAll(".barra-filtros-pastillas button[aria-label='Filtros guardados']")
            .Should().ContainSingle());
        var boton = cut.Find(".barra-filtros-pastillas button[aria-label='Filtros guardados']");
        boton.GetAttribute("data-keytip").Should().Be("G");
        var botones = cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador").ToList();
        var mas = botones.FindIndex(b => b.GetAttribute("aria-label") == "Más filtros");
        if (mas >= 0)
            botones.FindIndex(b => b.GetAttribute("aria-label") == "Filtros guardados").Should().BeGreaterThan(mas);
        await boton.ClickAsync(new MouseEventArgs());
        cut.FindAll(".barra-filtros-pastillas [role=menuitem]").Select(e => e.TextContent.Trim())
            .Should().Contain("Guardar filtro");
        cut.FindAll(".barra-filtros-pastillas .menu-filtro-guardado").Should().HaveCount(conGuardados ? 1 : 0);
        if (conGuardados)
            cut.Find(".barra-filtros-pastillas [role=menu]").TextContent.Should().Contain("Vista guardada de prueba");
        else
            cut.Find(".barra-filtros-pastillas [role=menu]").TextContent.Should().Contain("No hay filtros guardados");
    }
}

public partial class FiltrosGuardadosEnListadosTests
{
    [Theory]
    [InlineData("empresas", false)]
    [InlineData("empresas", true)]
    [InlineData("centros", false)]
    [InlineData("centros", true)]
    [InlineData("subcontratas", false)]
    [InlineData("subcontratas", true)]
    [InlineData("vehiculos", false)]
    [InlineData("vehiculos", true)]
    [InlineData("proyectos", false)]
    [InlineData("proyectos", true)]
    [InlineData("visitas", false)]
    [InlineData("visitas", true)]
    [InlineData("gestiones", false)]
    [InlineData("gestiones", true)]
    public async Task La_pastilla_guardados_en_las_diez_se_ve_con_y_sin_guardados(string pantalla, bool conGuardados)
    {
        if (conGuardados) _mediador.FiltrosGuardados.Add(ComprobacionPastillaGuardados.Filtro());
        switch (pantalla)
        {
            case "empresas":
                await ComprobacionPastillaGuardados.ComprobarAsync(
                    Renderizar<CaeManager.Web.Features.Empresas.Pages.Empresas>(pantalla), conGuardados);
                break;
            case "centros":
                await ComprobacionPastillaGuardados.ComprobarAsync(
                    Renderizar<CaeManager.Web.Features.Centros.Pages.Centros>(pantalla), conGuardados);
                break;
            case "subcontratas":
                await ComprobacionPastillaGuardados.ComprobarAsync(
                    Renderizar<CaeManager.Web.Features.Subcontratas.Pages.Subcontratas>(pantalla), conGuardados);
                break;
            case "vehiculos":
                await ComprobacionPastillaGuardados.ComprobarAsync(
                    Renderizar<CaeManager.Web.Features.Vehiculos.Pages.Vehiculos>(pantalla), conGuardados);
                break;
            case "proyectos":
                await ComprobacionPastillaGuardados.ComprobarAsync(
                    Renderizar<CaeManager.Web.Features.Proyectos.Pages.Proyectos>($"proyectos?cliente={ClienteA}"), conGuardados);
                break;
            case "visitas":
                await ComprobacionPastillaGuardados.ComprobarAsync(
                    Renderizar<CaeManager.Web.Features.Visitas.Pages.Visitas>(pantalla), conGuardados);
                break;
            case "gestiones":
                var gestiones = Renderizar<CaeManager.Web.Features.Gestiones.Pages.Gestiones>(pantalla);
                gestiones.FindAll(".barra-filtros-pastillas button[aria-label='Más filtros']").Should().BeEmpty();
                await ComprobacionPastillaGuardados.ComprobarAsync(gestiones, conGuardados);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(pantalla));
        }
    }
}

public partial class TrabajadoresListaGen2Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_pastilla_guardados_en_las_diez_se_ve_con_y_sin_guardados(bool conGuardados)
    {
        var mediador = new MediatorFalso();
        if (conGuardados) mediador.FiltrosGuardados.Add(ComprobacionPastillaGuardados.Filtro());
        await ComprobacionPastillaGuardados.ComprobarAsync(Renderizar(mediador), conGuardados);
    }
}

public partial class ClientesListaGen2Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_pastilla_guardados_en_las_diez_se_ve_con_y_sin_guardados(bool conGuardados)
    {
        var mediador = new MediatorFalso();
        if (conGuardados) mediador.FiltrosGuardados.Add(ComprobacionPastillaGuardados.Filtro());
        await ComprobacionPastillaGuardados.ComprobarAsync(Renderizar(mediador), conGuardados);
    }
}

public partial class DocumentosGen2Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_pastilla_guardados_en_las_diez_se_ve_con_y_sin_guardados(bool conGuardados)
    {
        var mediador = new MediadorControlado();
        if (conGuardados)
            mediador.Interceptar = p => p is ObtenerFiltrosGuardadosQuery
                ? Task.FromResult<object?>(new[] { ComprobacionPastillaGuardados.Filtro() }) : null;
        await ComprobacionPastillaGuardados.ComprobarAsync(Renderizar(mediador).Cut, conGuardados);
    }
}