using AngleSharp.Dom;
using Bunit;
using CaeManager.Domain.Gestiones;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Drawer de detalle de una Gestión (la vista rápida): inventario de lo que muestra, de sus enlaces, de sus rótulos y de sus
/// acciones por rol, y la jerarquía de su pie: un solo Primario, y «Eliminar» —destructivo— de menor peso y separado.
/// </summary>
public partial class GestionesListaGen2Tests
{
    private const string Drawer = "aside.vista-rapida-gestion";

    private static IReadOnlyList<string> TextosDeLosBotonesDelPie(IElement drawer) =>
        drawer.QuerySelectorAll(".pie-vista-rapida-gestion button").Select(b => b.TextContent.Trim()).ToList();

    [Fact]
    public async Task Drawer_inventario_de_una_Gestion_pendiente_con_escritura()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Gestion("Nuria Salas Ortiz", centro: "Planta farmacéutica", documento: "Reconocimiento médico") } });

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());

        var drawer = cut.Find(Drawer);
        drawer.QuerySelector(".etiqueta-vista-rapida-gestion")!.TextContent.Trim().Should().Be("Vista rápida · Gestión");
        drawer.QuerySelector(".nombre-vista-rapida-gestion")!.TextContent.Trim().Should().Be("Nuria Salas Ortiz");
        drawer.QuerySelector(".vista-rapida-centro")!.TextContent.Should().Contain("Planta farmacéutica");
        drawer.QuerySelector(".vista-rapida-tipo-documento")!.TextContent.Should().Contain("Reconocimiento médico");
        drawer.QuerySelectorAll(".enlaces-vista-rapida-gestion button").Select(b => b.TextContent.Trim())
            .Should().Equal("Abrir Trabajador 360 →", "Abrir Centro 360 →", "Ver la documentación del trabajador →");
        TextosDeLosBotonesDelPie(drawer).Should().Equal("Marcar completada", "Eliminar");
    }

    [Fact]
    public async Task Drawer_de_una_Gestion_completada_ofrece_reabrir_y_eliminar()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Gestion("Bruno Vidal Camps", EstadoGestion.Completada) } });

        await NombreEnLaFila(cut, "Bruno Vidal Camps").ClickAsync(new MouseEventArgs());

        TextosDeLosBotonesDelPie(cut.Find(Drawer)).Should().Equal("Reabrir gestión", "Eliminar");
    }

    [Fact]
    public async Task Drawer_Consulta_ve_el_detalle_y_los_enlaces_pero_no_el_pie_de_acciones()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var cut = Renderizar(new MediatorFalso { Almacen = { Gestion("Nuria Salas Ortiz") } });

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());

        var drawer = cut.Find(Drawer);
        drawer.QuerySelectorAll(".enlaces-vista-rapida-gestion button").Should().HaveCount(3);
        drawer.QuerySelector(".pie-vista-rapida-gestion").Should().BeNull("completar, reabrir y eliminar son de escritura");
    }

    /// <summary>A1: el Centro sale una sola vez en el cuerpo del drawer —la celda «Centro»—, no también en la tarjeta de Plataforma CAE.</summary>
    [Fact]
    public async Task Drawer_el_Centro_se_dice_una_sola_vez()
    {
        var (cut, _, _) = await VistaRapidaConCanales(CanalDePlataforma("app.nalanda.test/login"));

        var cuerpo = cut.Find(Drawer + " .cuerpo-vista-rapida-gestion").TextContent;
        System.Text.RegularExpressions.Regex.Matches(cuerpo, "Centro Logístico Sur").Should().HaveCount(1,
            "la tarjeta de Plataforma CAE y la celda «Centro» estaban seguidas y repetían el mismo Centro");
        cut.FindAll(Drawer + " .plataforma-centro-vista-rapida-gestion").Should().BeEmpty();
        cut.Find(Drawer + " .rejilla-vista-rapida-gestion .vista-rapida-centro").TextContent.Should().Contain("Centro Logístico Sur");
    }

    /// <summary>A2: un solo Primario en el pie; «Eliminar» es destructivo, de menor peso (no sólido) y separado del resto.</summary>
    [Fact]
    public async Task Drawer_pie_un_solo_primario_y_Eliminar_de_menor_peso_y_separado()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Gestion("Nuria Salas Ortiz") } });

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());

        var pie = cut.Find(Drawer + " .pie-vista-rapida-gestion");
        pie.QuerySelectorAll("button.boton-primario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Marcar completada");
        var eliminar = pie.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Eliminar");
        eliminar.ClassList.Should().NotContain("boton-primario").And.NotContain("boton-destructivo",
            "el rojo sólido compite con el Primario: el destructivo va de menor peso");
        eliminar.ClassList.Should().Contain("boton-secundario").And.Contain("eliminar-vista-rapida-gestion",
            "la clase propia lo pinta de peligro y lo separa del Primario");
    }
}
