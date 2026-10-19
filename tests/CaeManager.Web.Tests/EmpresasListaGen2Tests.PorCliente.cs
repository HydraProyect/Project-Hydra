using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El filtro «Cliente» y la agrupación «Por Cliente» del listado de Empresas. La pantalla solo pasa el Cliente a la
/// consulta y pinta lo que vuelve: que no enseñen una Empresa fuera de la cartera de gestión del usuario, ni un
/// Cliente fuera de su alcance, lo ata <c>ObtenerEmpresasPorClienteQueryTests</c> (Application, el handler real);
/// el doble del mediador solo lo reproduce (<c>ClientesAlcanzados</c>).
/// </summary>
public partial class EmpresasListaGen2Tests
{
    private sealed record Escenario(
        MediatorFalso Mediador, EmpresaListaDto Aragon, EmpresaListaDto Basauri, EmpresaListaDto Castro,
        ClienteDeEmpresaDto Umbrella, ClienteDeEmpresaDto Cyberdyne);

    /// <summary>
    /// Aragon sirve a Umbrella y a Cyberdyne; Basauri, a Cyberdyne; Castro no tiene Clientes. Los dos Clientes
    /// están en el alcance de lectura de quien mira.
    /// </summary>
    private static Escenario ConClientes()
    {
        var aragon = Empresa("Aragon Montajes S.L.");
        var basauri = Empresa("Basauri Obras S.L.");
        var castro = Empresa("Castro Servicios S.L.");
        var umbrella = ClienteEmpresarial("Umbrella Iberica S.A.", "B10000016");
        var cyberdyne = ClienteEmpresarial("Cyberdyne Iberica S.A.", "B10000024");
        var mediador = new MediatorFalso { Almacen = { aragon, basauri, castro } };
        mediador.ClientesDe[aragon.Id] = [umbrella, cyberdyne];
        mediador.ClientesDe[basauri.Id] = [cyberdyne];
        mediador.ClientesVisibles.AddRange([new ClienteSelectorDto(umbrella.Id, umbrella.RazonSocial), new ClienteSelectorDto(cyberdyne.Id, cyberdyne.RazonSocial)]);
        return new Escenario(mediador, aragon, basauri, castro, umbrella, cyberdyne);
    }

    private static string ParametroDeUrl(IRenderedComponent<Empresas> cut, string nombre) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(cut.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri).Query)[nombre] ?? string.Empty;

    [Fact]
    public void Con_Clientes_a_su_alcance_la_barra_ofrece_el_filtro_Cliente_y_la_agrupacion()
    {
        var cut = Renderizar(ConClientes().Mediador);

        Pastilla(cut, "Cliente").Should().NotBeNull();
        Pastilla(cut, "Agrupar").Should().NotBeNull();
    }

    [Fact]
    public void Un_usuario_de_portal_no_ve_ni_filtro_Cliente_ni_agrupacion_aunque_la_URL_los_pida()
    {
        var e = ConClientes();
        // Aunque tuviera Clientes a su alcance de lectura: el servidor le devuelve «ninguna Empresa» por Cliente.
        var cut = RenderizarComo(e.Mediador, Roles.Cliente, $"empresas?cliente={e.Cyberdyne.Id}&agrupar=cliente");

        cut.FindAll(".menu-acciones-disparador-pastilla").Select(b => b.GetAttribute("aria-label"))
            .Should().NotContain(["Cliente", "Agrupar"]);
        e.Mediador.Enviadas.OfType<ObtenerClientesParaSelectorQuery>().Should().BeEmpty("no se carga lo que no se ofrece");
        UltimaConsulta(e.Mediador).ClienteEmpresarialId.Should().BeNull("la URL no activa el filtro");
        UltimaConsulta(e.Mediador).ConClientePrincipal.Should().BeFalse();
        cut.FindAll(".tarjeta-fila-acordeon").Should().HaveCount(3, "control positivo: la lista de sus Empresas se ve entera");
        cut.FindAll(".cabecera-grupo-empresas").Should().BeEmpty();
    }

    [Fact]
    public void Un_rol_de_gestion_si_ve_las_dos_pastillas_con_el_mismo_arnes()
    {
        var cut = RenderizarComo(ConClientes().Mediador, Roles.Consulta);

        Pastilla(cut, "Cliente").Should().NotBeNull("control positivo del test del rol Cliente: Consulta opera desde gestión");
        Pastilla(cut, "Agrupar").Should().NotBeNull();
    }

    [Fact]
    public void Sin_Clientes_a_su_alcance_no_hay_ni_filtro_ni_agrupacion_y_la_lista_sigue_funcionando()
    {
        var mediador = ConClientes().Mediador;
        mediador.ClientesVisibles.Clear();
        var cut = Renderizar(mediador);

        cut.FindAll(".menu-acciones-disparador-pastilla").Select(b => b.GetAttribute("aria-label"))
            .Should().NotContain(["Cliente", "Agrupar"]);
        cut.FindAll(".tarjeta-fila-acordeon").Should().HaveCount(3);
    }

    [Fact]
    public async Task Elegir_un_Cliente_filtra_la_consulta_la_url_la_exportacion_y_pone_un_chip_que_se_quita()
    {
        var e = ConClientes();
        var cut = Renderizar(e.Mediador);

        await ElegirEnLaPastilla(cut, "Cliente", e.Cyberdyne.RazonSocial);

        UltimaConsulta(e.Mediador).ClienteEmpresarialId.Should().Be(e.Cyberdyne.Id);
        cut.WaitForAssertion(() => RazonesSociales(cut).Should().Equal(["Aragon Montajes S.L.", "Basauri Obras S.L."],
            "solo las Empresas que prestan servicio a ese Cliente"));
        ParametroDeUrl(cut, "cliente").Should().Be(e.Cyberdyne.Id.ToString());
        cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href")
            .Should().Contain($"cliente={e.Cyberdyne.Id}", "exportar la vista filtrada exporta lo que se ve");
        TextosDeLosChips(cut).Should().Contain("Cliente: " + e.Cyberdyne.RazonSocial);

        await cut.FindAll(".barra-filtros-pastillas .chip-filtro button").Single(b => b.GetAttribute("aria-label") == "Quitar filtro de Cliente")
            .ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UltimaConsulta(e.Mediador).ClienteEmpresarialId.Should().BeNull());
        ParametroDeUrl(cut, "cliente").Should().BeEmpty();
        RazonesSociales(cut).Should().HaveCount(3);
    }

    [Fact]
    public async Task Limpiar_todo_tambien_quita_el_filtro_Cliente()
    {
        var e = ConClientes();
        var cut = Renderizar(e.Mediador);
        await ElegirEnLaPastilla(cut, "Cliente", e.Umbrella.RazonSocial);
        cut.WaitForAssertion(() => RazonesSociales(cut).Should().Equal(["Aragon Montajes S.L."]));

        await cut.FindAll(".barra-filtros-pastillas button").Single(b => b.TextContent.Trim() == "Limpiar todo").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UltimaConsulta(e.Mediador).ClienteEmpresarialId.Should().BeNull());
        ParametroDeUrl(cut, "cliente").Should().BeEmpty();
    }

    [Fact]
    public void Un_cliente_de_la_url_que_no_esta_entre_los_visibles_no_se_aplica()
    {
        var e = ConClientes();
        var cut = Renderizar(e.Mediador, $"empresas?cliente={Guid.NewGuid()}");

        UltimaConsulta(e.Mediador).ClienteEmpresarialId.Should().BeNull("un Cliente fuera del alcance de lectura no filtra");
        RazonesSociales(cut).Should().HaveCount(3);
    }

    [Fact]
    public void Un_cliente_visible_de_la_url_filtra_desde_la_primera_carga()
    {
        var e = ConClientes();
        var cut = Renderizar(e.Mediador, $"empresas?cliente={e.Umbrella.Id}");

        UltimaConsulta(e.Mediador).ClienteEmpresarialId.Should().Be(e.Umbrella.Id);
        RazonesSociales(cut).Should().Equal(["Aragon Montajes S.L."]);
    }

    [Fact]
    public async Task Agrupar_Por_Cliente_pide_el_orden_y_el_Cliente_principal_y_titula_cada_grupo_con_su_recuento()
    {
        var e = ConClientes();
        var cut = Renderizar(e.Mediador);
        cut.FindAll(".cabecera-grupo-empresas").Should().BeEmpty("sin agrupar no hay títulos de grupo");

        await ElegirEnLaPastilla(cut, "Agrupar", "Por Cliente");

        var consulta = UltimaConsulta(e.Mediador);
        consulta.OrdenarPor.Should().Be(nameof(EmpresaListaDto.ClientePrincipal));
        consulta.ConClientePrincipal.Should().BeTrue();
        ParametroDeUrl(cut, "agrupar").Should().Be("cliente");
        cut.WaitForAssertion(() => cut.FindAll(".cabecera-grupo-empresas").Select(g => g.TextContent.Trim()).Should().Equal(
            ["Cyberdyne Iberica S.A.· 2", "Sin Cliente visible· 1"],
            "Aragon sirve a dos Clientes y sale una sola vez, bajo el primero por razón social; las sin Cliente, al final"));
        RazonesSociales(cut).Should().Equal(["Aragon Montajes S.L.", "Basauri Obras S.L.", "Castro Servicios S.L."]);
        cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("href").Should().NotContain("agrupar");

        await ElegirEnLaPastilla(cut, "Agrupar", "Sin agrupar");

        cut.WaitForAssertion(() => cut.FindAll(".cabecera-grupo-empresas").Should().BeEmpty());
        UltimaConsulta(e.Mediador).ConClientePrincipal.Should().BeFalse();
        ParametroDeUrl(cut, "agrupar").Should().BeEmpty();
    }

    [Fact]
    public void Una_agrupacion_de_la_url_se_aplica_desde_la_primera_carga()
    {
        var e = ConClientes();
        var cut = Renderizar(e.Mediador, "empresas?agrupar=cliente");

        UltimaConsulta(e.Mediador).ConClientePrincipal.Should().BeTrue();
        cut.FindAll(".cabecera-grupo-empresas").Should().HaveCount(2);
    }

    /// <summary>
    /// Una Empresa fuera de la cartera de gestión no sale al filtrar por Cliente aunque tenga la relación, y no
    /// lleva Cliente al agrupar: el doble reproduce lo que hace el handler real (ver el resumen de la clase).
    /// </summary>
    [Fact]
    public async Task Una_Empresa_fuera_de_la_cartera_de_gestion_no_sale_por_Cliente_ni_lleva_Cliente_al_agrupar()
    {
        var e = ConClientes();
        e.Mediador.FueraDeGestion.Add(e.Basauri.Id);
        var cut = Renderizar(e.Mediador);

        await ElegirEnLaPastilla(cut, "Cliente", e.Cyberdyne.RazonSocial);
        cut.WaitForAssertion(() => RazonesSociales(cut).Should().Equal(["Aragon Montajes S.L."]));

        await ElegirEnLaPastilla(cut, "Agrupar", "Por Cliente");
        cut.WaitForAssertion(() => cut.FindAll(".cabecera-grupo-empresas").Select(g => g.TextContent.Trim()).Should().Equal(["Cyberdyne Iberica S.A.· 1"]));
        cut.Markup.Should().NotContain("Basauri Obras S.L.");
    }

    [Fact]
    public void Un_guardado_en_la_vista_rapida_con_la_agrupacion_puesta_conserva_el_Cliente_principal()
    {
        var e = ConClientes();
        var cut = Renderizar(e.Mediador, "empresas?agrupar=cliente");
        _ = cut.InvokeAsync(() => Services.GetRequiredService<CaeManager.Web.Components.Workspace.ContextWorkspaceService>()
            .NotificarEntidadGuardada(CaeManager.Web.Components.Workspace.EntidadWorkspace.Empresa, e.Aragon.Id));

        cut.WaitForAssertion(() => UltimaConsulta(e.Mediador).EmpresaId.Should().Be(e.Aragon.Id));
        UltimaConsulta(e.Mediador).ConClientePrincipal.Should().BeTrue("la fila refrescada sigue bajo su grupo");
        cut.FindAll(".cabecera-grupo-empresas").Should().HaveCount(2);
    }
}