using Bunit;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La sección Documentos tiene UNA sola barra de pestañas, la misma en todas sus pantallas y en el mismo sitio
/// (bajo el título y las acciones de la página): este componente es esa barra. Aquí se fija qué pinta, cuál
/// marca como activa según la pantalla, a quién le oculta «Incidencias» y adónde lleva cada pestaña. Que cada
/// pantalla la pinte, y que el marco ya no pinte otra, lo fijan las pruebas de cada pantalla y
/// <c>BarraSeccionTests</c>.
/// </summary>
public class BarraPestanasDocumentosTests : BunitContext
{
    internal static readonly string[] SeisPestanas =
        ["Estado", "Plataformas CAE", "Reclamaciones", "Preventivo", "Revisión IA", "Plantillas"];

    internal static readonly string[] SietePestanas = [.. SeisPestanas, "Incidencias"];

    public BarraPestanasDocumentosTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    /// <summary>El rótulo de una pestaña sin la píldora de recuento ni el glifo: solo sus nodos de texto directos.</summary>
    internal static string Rotulo(AngleSharp.Dom.IElement pestana) =>
        string.Concat(pestana.ChildNodes.Where(n => n.NodeType == AngleSharp.Dom.NodeType.Text).Select(n => n.TextContent)).Trim();

    internal static IReadOnlyList<string> Rotulos<T>(IRenderedComponent<T> cut) where T : IComponent =>
        [.. cut.FindAll("[role=tablist] [role=tab]").Select(Rotulo)];

    internal static string? Activa<T>(IRenderedComponent<T> cut) where T : IComponent =>
        cut.FindAll("[role=tablist] [role=tab]").Where(p => p.GetAttribute("aria-selected") == "true").Select(Rotulo).SingleOrDefault();

    private IRenderedComponent<BarraPestanasDocumentos> Pintar(
        string rol, string activa, EventCallback<string>? alCambiar = null)
    {
        this.ConRolDeEscritura(rol);
        return Render<BarraPestanasDocumentos>(p =>
        {
            p.Add(x => x.PestanaActiva, activa);
            if (alCambiar is { } cambio) p.Add(x => x.PestanaActivaChanged, cambio);
            p.AddChildContent("<p id=\"contenido-de-prueba\">contenido</p>");
        });
    }

    [Theory]
    [InlineData("listado", "Estado")]
    [InlineData("plataforma", "Plataformas CAE")]
    [InlineData("reclamaciones", "Reclamaciones")]
    [InlineData("sugerencias", "Preventivo")]
    [InlineData("revision-ia", "Revisión IA")]
    [InlineData("plantillas", "Plantillas")]
    [InlineData("incidencias", "Incidencias")]
    public void Pinta_una_sola_tira_con_las_siete_pestanas_y_marca_activa_la_de_la_pantalla(string id, string rotuloActivo)
    {
        var cut = Pintar(Roles.Administrador, id);

        cut.FindAll("[role=tablist]").Should().ContainSingle();
        Rotulos(cut).Should().Equal(SietePestanas);
        Activa(cut).Should().Be(rotuloActivo);
        cut.Find("[role=tabpanel] #contenido-de-prueba").TextContent.Should().Be("contenido",
            "el contenido de la pantalla vive dentro del panel de la pestaña activa, bajo la tira");
    }

    [Theory]
    [InlineData(Roles.Administrador, true, true)]
    [InlineData(Roles.DireccionCae, true, true)]
    [InlineData(Roles.CoordinadorCae, true, true)]
    [InlineData(Roles.GestorCae, true, true)]
    [InlineData(Roles.Consulta, false, true)]
    [InlineData(Roles.Cliente, false, false)]
    public void Revision_IA_e_Incidencias_conservan_la_regla_de_rol_que_ya_tenian_en_la_barra_de_seccion(
        string rol, bool conRevisionIa, bool conIncidencias)
    {
        var cut = Pintar(rol, "listado");

        var esperadas = SietePestanas
            .Where(r => (r != "Revisión IA" || conRevisionIa) && (r != "Incidencias" || conIncidencias))
            .ToList();
        Rotulos(cut).Should().Equal(esperadas);
    }

    [Theory]
    [InlineData("Estado", "/documentos")]
    [InlineData("Plataformas CAE", "/documentos?pestana=plataforma")]
    [InlineData("Reclamaciones", "/documentos?pestana=reclamaciones")]
    [InlineData("Preventivo", "/documentos?pestana=sugerencias")]
    [InlineData("Revisión IA", "/documentos?pestana=revision-ia")]
    [InlineData("Plantillas", "/documentos?pestana=plantillas")]
    public async Task Desde_una_pantalla_hermana_cada_pestana_lleva_a_su_vista_de_Documentos(string rotulo, string destino)
    {
        var cut = Pintar(Roles.Administrador, "incidencias");

        await cut.FindAll("[role=tab]").Single(p => Rotulo(p) == rotulo).ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith(destino);
    }

    [Fact]
    public async Task Desde_Documentos_Incidencias_lleva_a_su_pantalla_sin_avisar_a_la_pagina()
    {
        var elegidas = new List<string>();
        var cut = Pintar(Roles.Administrador, "plantillas", EventCallback.Factory.Create<string>(this, elegidas.Add));

        await cut.FindAll("[role=tab]").Single(p => Rotulo(p) == "Incidencias").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/incidencias");
        elegidas.Should().BeEmpty("Incidencias es una pantalla hermana, no una vista de Documentos");
    }

    [Fact]
    public async Task Documentos_recibe_el_cambio_de_vista_sin_navegar_la_barra()
    {
        var elegidas = new List<string>();
        var cut = Pintar(Roles.Administrador, "listado", EventCallback.Factory.Create<string>(this, elegidas.Add));
        var antes = Services.GetRequiredService<NavigationManager>().Uri;

        await cut.FindAll("[role=tab]").Single(p => Rotulo(p) == "Plantillas").ClickAsync(new MouseEventArgs());

        elegidas.Should().Equal("plantillas");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Be(antes);
    }
}
