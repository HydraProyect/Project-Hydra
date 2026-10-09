using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La fila de /clientes (Clientes empresariales) se refresca en sitio tras guardar en la vista
/// rápida (panel del Context Workspace, que vive en MainLayout y guarda sin pasar por la página).
/// El panel avisa por <see cref="ContextWorkspaceService.OnEntidadGuardada"/> y la página vuelve
/// a pedir SOLO esa fila, con el filtro por id de la consulta de lista. Aquí no se monta el
/// panel: el aviso se emite a mano sobre el servicio que la página tiene inyectado.
/// </summary>
public partial class ClientesListaGen2Tests
{
    /// <summary>
    /// El aviso tal como lo emite el panel. La página lo atiende con un <c>InvokeAsync</c> que no
    /// se espera: se emite dentro del despachador y lo que pinte se afirma con WaitForAssertion.
    /// </summary>
    private Task AvisarGuardadoAsync(IRenderedComponent<Clientes> cut, EntidadWorkspace tipo, Guid id) =>
        cut.InvokeAsync(() => Services.GetRequiredService<ContextWorkspaceService>().NotificarEntidadGuardada(tipo, id));

    /// <summary>Cambia lo guardado en el doble, como haría el comando de edición del panel.</summary>
    private static void RenombrarEnElAlmacen(MediatorFalso mediador, Guid id, string razonSocial)
    {
        var indice = mediador.Almacen.FindIndex(c => c.Id == id);
        mediador.Almacen[indice] = mediador.Almacen[indice] with { RazonSocial = razonSocial };
    }

    private static MediatorFalso ConVeinticincoClientes()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Cliente($"Cliente {i:00}"));
        return mediador;
    }

    private static async Task IrALaPagina2Async(IRenderedComponent<Clientes> cut)
    {
        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().HaveCount(5).And.StartWith("Cliente 21"));
    }

    private static List<IElement> FilasConDatos(IRenderedComponent<Clientes> cut) =>
        cut.FindAll("tbody tr").Where(tr => tr.QuerySelector(".enlace-nombre-fila") is not null).ToList();

    /// <summary>
    /// La razón social nueva mandaría la fila al final con el orden de la lista: que siga la
    /// primera es lo que distingue «sustituir en sitio» de «recargar la página».
    /// </summary>
    [Fact]
    public async Task El_aviso_de_guardado_sustituye_la_fila_en_sitio_con_una_sola_consulta_por_id()
    {
        var alfa = Cliente("Alfa Montajes S.L.");
        var mediador = new MediatorFalso { Almacen = { alfa, Cliente("Beta Talleres Coop.") } };
        var cut = Renderizar(mediador);
        NombresDeLasFilas(cut).Should().Equal(["Alfa Montajes S.L.", "Beta Talleres Coop."], "punto de partida");
        var consultasAntes = ConsultasDeLista(mediador);

        RenombrarEnElAlmacen(mediador, alfa.Id, "Zeta Montajes S.L.");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Cliente, alfa.Id);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Zeta Montajes S.L.", "Beta Talleres Coop."],
            "la fila enseña el dato nuevo y no cambia de sitio"));
        UltimaConsulta(mediador).Id.Should().Be(alfa.Id, "se pide solo esa fila");
        UltimaConsulta(mediador).Busqueda.Should().BeNull();
        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "la consulta por id y ninguna de página detrás");
        mediador.Enviadas.OfType<ObtenerClientesQuery>().Count(q => q.Id is null).Should().Be(consultasAntes,
            "ninguna consulta de página nueva");
    }

    [Fact]
    public async Task El_aviso_de_guardado_conserva_la_pagina_la_seleccion_multiple_y_la_fila_enfocada()
    {
        var mediador = ConVeinticincoClientes();
        var editado = mediador.Almacen.Single(c => c.RazonSocial == "Cliente 23").Id;
        var cut = Renderizar(mediador);
        await IrALaPagina2Async(cut);
        await AlternarSeleccionMultiple(cut);
        await Fila(cut, "Cliente 22").QuerySelector("input[type=checkbox]")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await Fila(cut, "Cliente 23").QuerySelector("input[type=checkbox]")!.ChangeAsync(new ChangeEventArgs { Value = true });
        var atajos = cut.FindComponent<AtajosListaTeclado>();
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        (FilasConDatos(cut)[0].ClassName ?? string.Empty).Should().Contain("fila-enfocada", "punto de partida: la j enfocó la primera fila");
        var consultasAntes = ConsultasDeLista(mediador);

        RenombrarEnElAlmacen(mediador, editado, "Renombrado");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Cliente, editado);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(
            "Cliente 21", "Cliente 22", "Renombrado", "Cliente 24", "Cliente 25"));
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2");
        FilasConDatos(cut).Select(tr => tr.QuerySelector("input[type=checkbox]")!.HasAttribute("checked")).Should().Equal(
            [false, true, true, false, false], "la selección sigue marcada, también la de la fila sustituida");
        FilasConDatos(cut).Select(tr => (tr.ClassName ?? string.Empty).Contains("fila-enfocada")).Should().Equal(true, false, false, false, false);
        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "una carga de página habría limpiado selección y foco");
        UltimaConsulta(mediador).Id.Should().Be(editado);
    }

    /// <summary>
    /// La búsqueda activa es «Alfa» y el guardado cambia la razón social: la fila ya no casa con
    /// el filtro, pero permanece hasta la siguiente carga, y el filtro no se toca.
    /// </summary>
    [Fact]
    public async Task La_fila_permanece_aunque_el_cambio_la_saque_del_filtro_activo()
    {
        var alfa = Cliente("Alfa Montajes S.L.");
        var mediador = new MediatorFalso { Almacen = { alfa, Cliente("Beta Talleres Coop.") } };
        var cut = Renderizar(mediador, "clientes?q=Alfa");
        NombresDeLasFilas(cut).Should().Equal(["Alfa Montajes S.L."], "punto de partida: el filtro deja una fila");

        RenombrarEnElAlmacen(mediador, alfa.Id, "Zeta Montajes S.L.");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Cliente, alfa.Id);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal("Zeta Montajes S.L."));
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Alfa");
        mediador.Enviadas.OfType<ObtenerClientesQuery>().Last(q => q.Id is null).Busqueda.Should().Be("Alfa",
            "la última consulta de página sigue siendo la del filtro");
    }

    /// <summary>
    /// Tres avisos que no son de esta página: otro tipo de entidad con un id que sí está a la
    /// vista, un Cliente empresarial que no existe y uno que está en la página 1 mientras se
    /// mira la 2. El aviso se atiende en línea dentro del despachador y el doble responde en
    /// síncrono, así que al volver de cada uno ya no queda nada pendiente; el control positivo
    /// del final demuestra que el contador ve la consulta cuando sí la hay.
    /// </summary>
    [Fact]
    public async Task Un_aviso_de_otra_entidad_o_de_un_id_fuera_de_la_pagina_no_consulta_nada()
    {
        var mediador = ConVeinticincoClientes();
        var dePagina1 = mediador.Almacen.Single(c => c.RazonSocial == "Cliente 01").Id;
        var dePagina2 = mediador.Almacen.Single(c => c.RazonSocial == "Cliente 21").Id;
        var cut = Renderizar(mediador);
        await IrALaPagina2Async(cut);
        var enviadasAntes = mediador.Enviadas.Count;
        RenombrarEnElAlmacen(mediador, dePagina1, "Renombrado de la página 1");
        RenombrarEnElAlmacen(mediador, dePagina2, "Renombrado");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Empresa, dePagina2);
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Cliente, Guid.NewGuid());
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Cliente, dePagina1);

        mediador.Enviadas.Should().HaveCount(enviadasAntes, "ninguno de los tres avisos es de una fila de esta página");
        NombresDeLasFilas(cut).Should().StartWith("Cliente 21", "sin consulta no hay dato nuevo que pintar");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Cliente, dePagina2);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().StartWith("Renombrado"));
        mediador.Enviadas.Should().HaveCount(enviadasAntes + 1, "control positivo: el aviso de una fila a la vista sí consulta");
    }

    [Fact]
    public async Task Si_la_relectura_de_la_fila_falla_no_se_ensena_ningun_error_y_la_fila_se_queda()
    {
        var alfa = Cliente("Alfa Montajes S.L.");
        var mediador = new MediatorFalso { Almacen = { alfa, Cliente("Beta Talleres Coop.") } };
        var cut = Renderizar(mediador);
        var consultasAntes = ConsultasDeLista(mediador);
        mediador.Retener = peticion => peticion is ObtenerClientesQuery { Id: not null }
            ? Task.FromException<object>(new InvalidOperationException("Fallo simulado de la relectura."))
            : null;

        RenombrarEnElAlmacen(mediador, alfa.Id, "Zeta Montajes S.L.");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Cliente, alfa.Id);

        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "control positivo: la relectura se intentó");
        NombresDeLasFilas(cut).Should().Equal(["Alfa Montajes S.L.", "Beta Talleres Coop."], "la fila conserva el dato anterior");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("el guardado ya es firme: no hay error que enseñar");
        cut.Markup.Should().NotContain("Fallo simulado");

        // La página sigue viva: el siguiente aviso, ya sin fallo, sí sustituye la fila.
        mediador.Retener = null;
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Cliente, alfa.Id);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal("Zeta Montajes S.L.", "Beta Talleres Coop."));
    }
}
