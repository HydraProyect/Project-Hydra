using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Subcontratas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La fila de /subcontratas se refresca en sitio tras guardar en la vista rápida (panel del
/// Context Workspace, que vive en MainLayout y guarda sin pasar por la página). El panel avisa
/// por <see cref="ContextWorkspaceService.OnEntidadGuardada"/> y la página vuelve a pedir SOLO
/// esa fila, con el filtro por id de la consulta de lista. Aquí no se monta el panel: el aviso se
/// emite a mano sobre el servicio que la página tiene inyectado.
///
/// <para>
/// El doble de esta lista no ordena (devuelve el almacén tal cual): aquí «en sitio» lo sostiene
/// el recuento de consultas —una, por id, y ninguna de página—, no la posición de la fila.
/// </para>
/// </summary>
public partial class SubcontratasListaGen2Tests
{
    /// <summary>
    /// El aviso tal como lo emite el panel. La página lo atiende con un <c>InvokeAsync</c> que no
    /// se espera: se emite dentro del despachador y lo que pinte se afirma con WaitForAssertion.
    /// </summary>
    private Task AvisarGuardadoAsync(IRenderedComponent<Subcontratas> cut, EntidadWorkspace tipo, Guid id) =>
        cut.InvokeAsync(() => Services.GetRequiredService<ContextWorkspaceService>().NotificarEntidadGuardada(tipo, id));

    /// <summary>Cambia lo guardado en el almacén del doble, como haría el comando de edición del panel.</summary>
    private static void RenombrarEnElAlmacen(List<SubcontrataListaDto> almacen, Guid id, string razonSocial)
    {
        var indice = almacen.FindIndex(s => s.Id == id);
        almacen[indice] = almacen[indice] with { RazonSocial = razonSocial };
    }

    private static List<SubcontrataListaDto> VeinticincoSubcontratas() =>
        Enumerable.Range(1, 25).Select(i => Subcontrata($"Subcontrata {i:00}")).ToList();

    private static List<IElement> TarjetasDeSubcontrata(IRenderedComponent<Subcontratas> cut) =>
        cut.FindAll(".tarjeta-fila-acordeon").ToList();

    private static List<string> RazonesSociales(IRenderedComponent<Subcontratas> cut) =>
        TarjetasDeSubcontrata(cut).Select(t => t.QuerySelector(".enlace-nombre-fila")!.TextContent.Trim()).ToList();

    private static async Task IrALaPagina2Async(IRenderedComponent<Subcontratas> cut)
    {
        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => RazonesSociales(cut).Should().HaveCount(5).And.StartWith("Subcontrata 21"));
    }

    [Fact]
    public async Task El_aviso_de_guardado_sustituye_la_fila_en_sitio_con_una_sola_consulta_por_id()
    {
        var bidasoa = Subcontrata("Andamios Bidasoa S.L.");
        var almacen = new List<SubcontrataListaDto> { bidasoa, Subcontrata("Pinturas Urola S.L.") };
        var mediador = new MediatorFalso { Subcontratas = almacen };
        var cut = Renderizar(mediador);
        RazonesSociales(cut).Should().Equal(["Andamios Bidasoa S.L.", "Pinturas Urola S.L."], "punto de partida");
        var consultasAntes = ConsultasDeLista(mediador);

        RenombrarEnElAlmacen(almacen, bidasoa.Id, "Zubiak Andamios S.L.");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Subcontrata, bidasoa.Id);

        cut.WaitForAssertion(() => RazonesSociales(cut).Should().Equal(["Zubiak Andamios S.L.", "Pinturas Urola S.L."],
            "la fila enseña el dato nuevo en el mismo sitio"));
        var ultima = mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last();
        ultima.SubcontrataId.Should().Be(bidasoa.Id, "se pide solo esa fila");
        ultima.Busqueda.Should().BeNull();
        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "la consulta por id y ninguna de página detrás");
        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Count(q => q.SubcontrataId is null).Should().Be(consultasAntes,
            "ninguna consulta de página nueva");
    }

    /// <summary>Además de página, selección y foco, la fila desplegada sigue desplegada.</summary>
    [Fact]
    public async Task El_aviso_de_guardado_conserva_la_pagina_la_seleccion_multiple_la_fila_enfocada_y_la_fila_desplegada()
    {
        // El contenido del desplegable no es objeto de este test (y pide servicios de Documentos).
        ComponentFactories.AddStub<CaeManager.Web.Features.Subcontratas.Components.AcordeonTrabajadoresSubcontrata>();
        var almacen = VeinticincoSubcontratas();
        var editada = almacen.Single(s => s.RazonSocial == "Subcontrata 23").Id;
        var mediador = new MediatorFalso { Subcontratas = almacen };
        var cut = Renderizar(mediador);
        await IrALaPagina2Async(cut);
        await EncenderSeleccionMultiple(cut);
        await cut.Find("input[aria-label='Seleccionar la empresa Subcontrata 22']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.Find("input[aria-label='Seleccionar la empresa Subcontrata 23']").ChangeAsync(new ChangeEventArgs { Value = true });
        await TarjetasDeSubcontrata(cut)[2].QuerySelector(".boton-expandir-fila")!.ClickAsync(new MouseEventArgs());
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        cut.Find(".fila-enfocada .enlace-nombre-fila").TextContent.Trim().Should().Be("Subcontrata 21", "punto de partida: la j enfocó la primera fila");
        cut.FindAll(".boton-expandir-fila").Select(b => b.GetAttribute("aria-expanded")).Should().Equal(
            ["false", "false", "true", "false", "false"], "punto de partida: la tercera fila está desplegada");
        var enviadasAntes = mediador.Enviadas.Count;

        RenombrarEnElAlmacen(almacen, editada, "Renombrada");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Subcontrata, editada);

        cut.WaitForAssertion(() => RazonesSociales(cut).Should().Equal(
            "Subcontrata 21", "Subcontrata 22", "Renombrada", "Subcontrata 24", "Subcontrata 25"));
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2");
        cut.Find("input[aria-label='Seleccionar la empresa Subcontrata 22']").HasAttribute("checked").Should().BeTrue("la selección de otra fila sigue marcada");
        cut.Find("input[aria-label='Seleccionar la empresa Renombrada']").HasAttribute("checked").Should().BeTrue("la fila sustituida sigue seleccionada");
        cut.FindAll(".tarjeta-fila-acordeon-cabecera input[type=checkbox]").Count(c => c.HasAttribute("checked")).Should().Be(2);
        cut.Find(".fila-enfocada .enlace-nombre-fila").TextContent.Trim().Should().Be("Subcontrata 21");
        cut.FindAll(".boton-expandir-fila").Select(b => b.GetAttribute("aria-expanded")).Should().Equal(
            ["false", "false", "true", "false", "false"], "la fila sustituida sigue desplegada");
        mediador.Enviadas.Should().HaveCount(enviadasAntes + 1, "solo la consulta de esa fila");
        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last().SubcontrataId.Should().Be(editada);
    }

    /// <summary>
    /// La búsqueda activa es «Bidasoa» y el guardado cambia la razón social: la fila ya no casa
    /// con el filtro, pero permanece hasta la siguiente carga, y el filtro no se toca.
    /// </summary>
    [Fact]
    public async Task La_fila_permanece_aunque_el_cambio_la_saque_del_filtro_activo()
    {
        var bidasoa = Subcontrata("Andamios Bidasoa S.L.");
        var almacen = new List<SubcontrataListaDto> { bidasoa, Subcontrata("Pinturas Urola S.L.") };
        var mediador = new MediatorFalso { Subcontratas = almacen };
        var cut = Renderizar(mediador, busqueda: "Bidasoa");
        RazonesSociales(cut).Should().Equal(["Andamios Bidasoa S.L."], "punto de partida: el filtro deja una fila");

        RenombrarEnElAlmacen(almacen, bidasoa.Id, "Zubiak Andamios S.L.");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Subcontrata, bidasoa.Id);

        cut.WaitForAssertion(() => RazonesSociales(cut).Should().Equal("Zubiak Andamios S.L."));
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Bidasoa");
        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last(q => q.SubcontrataId is null).Busqueda.Should().Be("Bidasoa",
            "la última consulta de página sigue siendo la del filtro");
    }

    /// <summary>
    /// Tres avisos que no son de esta página: otro tipo de entidad con un id que sí está a la
    /// vista, una Subcontrata que no existe y una que está en la página 1 mientras se mira la 2.
    /// El aviso se atiende en línea dentro del despachador y el doble responde en síncrono, así
    /// que al volver de cada uno ya no queda nada pendiente; el control positivo del final
    /// demuestra que el contador ve la consulta cuando sí la hay.
    /// </summary>
    [Fact]
    public async Task Un_aviso_de_otra_entidad_o_de_un_id_fuera_de_la_pagina_no_consulta_nada()
    {
        var almacen = VeinticincoSubcontratas();
        var dePagina1 = almacen.Single(s => s.RazonSocial == "Subcontrata 01").Id;
        var dePagina2 = almacen.Single(s => s.RazonSocial == "Subcontrata 21").Id;
        var mediador = new MediatorFalso { Subcontratas = almacen };
        var cut = Renderizar(mediador);
        await IrALaPagina2Async(cut);
        var enviadasAntes = mediador.Enviadas.Count;
        RenombrarEnElAlmacen(almacen, dePagina1, "Renombrada de la página 1");
        RenombrarEnElAlmacen(almacen, dePagina2, "Renombrada");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Empresa, dePagina2);
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Subcontrata, Guid.NewGuid());
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Subcontrata, dePagina1);

        mediador.Enviadas.Should().HaveCount(enviadasAntes, "ninguno de los tres avisos es de una fila de esta página");
        RazonesSociales(cut).Should().StartWith("Subcontrata 21", "sin consulta no hay dato nuevo que pintar");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Subcontrata, dePagina2);

        cut.WaitForAssertion(() => RazonesSociales(cut).Should().StartWith("Renombrada"));
        mediador.Enviadas.Should().HaveCount(enviadasAntes + 1, "control positivo: el aviso de una fila a la vista sí consulta");
    }
}
