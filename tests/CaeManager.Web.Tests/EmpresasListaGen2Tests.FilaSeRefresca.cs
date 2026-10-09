using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La fila de /empresas se refresca en sitio tras guardar en la vista rápida (panel del Context
/// Workspace, que vive en MainLayout y guarda sin pasar por la página). El panel avisa por
/// <see cref="ContextWorkspaceService.OnEntidadGuardada"/> y la página vuelve a pedir SOLO esa
/// fila, con el filtro por id de la consulta de lista. Aquí no se monta el panel: el aviso se
/// emite a mano sobre el servicio que la página tiene inyectado.
/// </summary>
public partial class EmpresasListaGen2Tests
{
    /// <summary>
    /// El aviso tal como lo emite el panel. La página lo atiende con un <c>InvokeAsync</c> que no
    /// se espera: se emite dentro del despachador y lo que pinte se afirma con WaitForAssertion.
    /// </summary>
    private Task AvisarGuardadoAsync(IRenderedComponent<Empresas> cut, EntidadWorkspace tipo, Guid id) =>
        cut.InvokeAsync(() => Services.GetRequiredService<ContextWorkspaceService>().NotificarEntidadGuardada(tipo, id));

    /// <summary>Cambia lo guardado en el doble, como haría el comando de edición del panel.</summary>
    private static void RenombrarEnElAlmacen(MediatorFalso mediador, Guid id, string razonSocial)
    {
        var indice = mediador.Almacen.FindIndex(e => e.Id == id);
        mediador.Almacen[indice] = mediador.Almacen[indice] with { RazonSocial = razonSocial };
    }

    private static MediatorFalso ConVeinticincoEmpresas()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Empresa($"Empresa {i:00}"));
        return mediador;
    }

    private static List<IElement> Tarjetas(IRenderedComponent<Empresas> cut) => cut.FindAll(".tarjeta-fila-acordeon").ToList();

    private static List<string> RazonesSociales(IRenderedComponent<Empresas> cut) =>
        Tarjetas(cut).Select(t => t.QuerySelector(".enlace-nombre-fila")!.TextContent.Trim()).ToList();

    private static IElement Tarjeta(IRenderedComponent<Empresas> cut, string razonSocial) =>
        Tarjetas(cut).Single(t => t.QuerySelector(".enlace-nombre-fila")!.TextContent.Trim() == razonSocial);

    private static async Task IrALaPagina2Async(IRenderedComponent<Empresas> cut)
    {
        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => RazonesSociales(cut).Should().HaveCount(5).And.StartWith("Empresa 21"));
    }

    /// <summary>
    /// La razón social nueva mandaría la fila al final con el orden de la lista: que siga la
    /// primera es lo que distingue «sustituir en sitio» de «recargar la página».
    /// </summary>
    [Fact]
    public async Task El_aviso_de_guardado_sustituye_la_fila_en_sitio_con_una_sola_consulta_por_id()
    {
        var nervion = Empresa("Aislamientos Nervión S.L.");
        var mediador = new MediatorFalso { Almacen = { nervion, Empresa("Refrielectric S.A.") } };
        var cut = Renderizar(mediador);
        RazonesSociales(cut).Should().Equal(["Aislamientos Nervión S.L.", "Refrielectric S.A."], "punto de partida");
        var consultasAntes = ConsultasDeLista(mediador);

        RenombrarEnElAlmacen(mediador, nervion.Id, "Zubiak Aislamientos S.L.");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Empresa, nervion.Id);

        cut.WaitForAssertion(() => RazonesSociales(cut).Should().Equal(["Zubiak Aislamientos S.L.", "Refrielectric S.A."],
            "la fila enseña el dato nuevo y no cambia de sitio"));
        UltimaConsulta(mediador).EmpresaId.Should().Be(nervion.Id, "se pide solo esa fila");
        UltimaConsulta(mediador).Busqueda.Should().BeNull();
        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "la consulta por id y ninguna de página detrás");
        mediador.Enviadas.OfType<ObtenerEmpresasQuery>().Count(q => q.EmpresaId is null).Should().Be(consultasAntes,
            "ninguna consulta de página nueva");
    }

    /// <summary>Además de página, selección y foco, la fila desplegada (sus Clientes empresariales) sigue desplegada.</summary>
    [Fact]
    public async Task El_aviso_de_guardado_conserva_la_pagina_la_seleccion_multiple_la_fila_enfocada_y_la_fila_desplegada()
    {
        var mediador = ConVeinticincoEmpresas();
        var editada = mediador.Almacen.Single(e => e.RazonSocial == "Empresa 23").Id;
        var cut = Renderizar(mediador);
        await IrALaPagina2Async(cut);
        await AlternarSeleccionMultiple(cut);
        await cut.Find("input[aria-label='Seleccionar Empresa 22']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.Find("input[aria-label='Seleccionar Empresa 23']").ChangeAsync(new ChangeEventArgs { Value = true });
        await Tarjeta(cut, "Empresa 23").QuerySelector(".boton-expandir-fila")!.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Tarjeta(cut, "Empresa 23").QuerySelector(".boton-expandir-fila")!.GetAttribute("aria-expanded")
            .Should().Be("true", "punto de partida: la fila está desplegada"));
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        cut.Find(".fila-enfocada .enlace-nombre-fila").TextContent.Trim().Should().Be("Empresa 21", "punto de partida: la j enfocó la primera fila");
        var enviadasAntes = mediador.Enviadas.Count;

        RenombrarEnElAlmacen(mediador, editada, "Renombrada");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Empresa, editada);

        cut.WaitForAssertion(() => RazonesSociales(cut).Should().Equal(
            "Empresa 21", "Empresa 22", "Renombrada", "Empresa 24", "Empresa 25"));
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2");
        cut.Find("input[aria-label='Seleccionar Empresa 22']").HasAttribute("checked").Should().BeTrue("la selección de otra fila sigue marcada");
        cut.Find("input[aria-label='Seleccionar Renombrada']").HasAttribute("checked").Should().BeTrue("la fila sustituida sigue seleccionada");
        cut.FindAll(".tarjeta-fila-acordeon-cabecera input[type=checkbox]").Count(c => c.HasAttribute("checked")).Should().Be(2);
        cut.Find(".fila-enfocada .enlace-nombre-fila").TextContent.Trim().Should().Be("Empresa 21");
        cut.FindAll(".boton-expandir-fila").Select(b => b.GetAttribute("aria-expanded")).Should().Equal(
            ["false", "false", "true", "false", "false"], "la fila sustituida sigue desplegada");
        mediador.Enviadas.Should().HaveCount(enviadasAntes + 1, "solo la consulta de esa fila: ni página ni Clientes empresariales otra vez");
        UltimaConsulta(mediador).EmpresaId.Should().Be(editada);
    }

    /// <summary>
    /// La búsqueda activa es «Nervion» y el guardado cambia la razón social: la fila ya no casa
    /// con el filtro, pero permanece hasta la siguiente carga, y el filtro no se toca.
    /// </summary>
    [Fact]
    public async Task La_fila_permanece_aunque_el_cambio_la_saque_del_filtro_activo()
    {
        var nervion = Empresa("Aislamientos Nervion S.L.");
        var mediador = new MediatorFalso { Almacen = { nervion, Empresa("Refrielectric S.A.") } };
        var cut = Renderizar(mediador, "empresas?q=Nervion");
        RazonesSociales(cut).Should().Equal(["Aislamientos Nervion S.L."], "punto de partida: el filtro deja una fila");

        RenombrarEnElAlmacen(mediador, nervion.Id, "Zubiak Aislamientos S.L.");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Empresa, nervion.Id);

        cut.WaitForAssertion(() => RazonesSociales(cut).Should().Equal("Zubiak Aislamientos S.L."));
        Navegacion.Uri.Should().Contain("q=Nervion");
        mediador.Enviadas.OfType<ObtenerEmpresasQuery>().Last(q => q.EmpresaId is null).Busqueda.Should().Be("Nervion",
            "la última consulta de página sigue siendo la del filtro");
    }

    /// <summary>
    /// Tres avisos que no son de esta página: otro tipo de entidad con un id que sí está a la
    /// vista, una Empresa que no existe y una que está en la página 1 mientras se mira la 2.
    /// El aviso se atiende en línea dentro del despachador y el doble responde en síncrono, así
    /// que al volver de cada uno ya no queda nada pendiente; el control positivo del final
    /// demuestra que el contador ve la consulta cuando sí la hay.
    /// </summary>
    [Fact]
    public async Task Un_aviso_de_otra_entidad_o_de_un_id_fuera_de_la_pagina_no_consulta_nada()
    {
        var mediador = ConVeinticincoEmpresas();
        var dePagina1 = mediador.Almacen.Single(e => e.RazonSocial == "Empresa 01").Id;
        var dePagina2 = mediador.Almacen.Single(e => e.RazonSocial == "Empresa 21").Id;
        var cut = Renderizar(mediador);
        await IrALaPagina2Async(cut);
        var enviadasAntes = mediador.Enviadas.Count;
        RenombrarEnElAlmacen(mediador, dePagina1, "Renombrada de la página 1");
        RenombrarEnElAlmacen(mediador, dePagina2, "Renombrada");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Cliente, dePagina2);
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Empresa, Guid.NewGuid());
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Empresa, dePagina1);

        mediador.Enviadas.Should().HaveCount(enviadasAntes, "ninguno de los tres avisos es de una fila de esta página");
        RazonesSociales(cut).Should().StartWith("Empresa 21", "sin consulta no hay dato nuevo que pintar");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Empresa, dePagina2);

        cut.WaitForAssertion(() => RazonesSociales(cut).Should().StartWith("Renombrada"));
        mediador.Enviadas.Should().HaveCount(enviadasAntes + 1, "control positivo: el aviso de una fila a la vista sí consulta");
    }

    [Fact]
    public async Task Si_la_relectura_de_la_fila_falla_no_se_ensena_ningun_error_y_la_fila_se_queda()
    {
        var nervion = Empresa("Aislamientos Nervión S.L.");
        var mediador = new MediatorFalso { Almacen = { nervion, Empresa("Refrielectric S.A.") } };
        var cut = Renderizar(mediador);
        var consultasAntes = ConsultasDeLista(mediador);
        mediador.Retener = peticion => peticion is ObtenerEmpresasQuery { EmpresaId: not null }
            ? Task.FromException<object>(new InvalidOperationException("Fallo simulado de la relectura."))
            : null;

        RenombrarEnElAlmacen(mediador, nervion.Id, "Zubiak Aislamientos S.L.");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Empresa, nervion.Id);

        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "control positivo: la relectura se intentó");
        RazonesSociales(cut).Should().Equal(["Aislamientos Nervión S.L.", "Refrielectric S.A."], "la fila conserva el dato anterior");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("el guardado ya es firme: no hay error que enseñar");
        cut.Markup.Should().NotContain("Fallo simulado");

        // La página sigue viva: el siguiente aviso, ya sin fallo, sí sustituye la fila.
        mediador.Retener = null;
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Empresa, nervion.Id);

        cut.WaitForAssertion(() => RazonesSociales(cut).Should().Equal("Zubiak Aislamientos S.L.", "Refrielectric S.A."));
    }
}
