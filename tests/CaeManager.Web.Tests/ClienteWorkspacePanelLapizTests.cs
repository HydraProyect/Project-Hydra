using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerCentrosDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerResumenCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08) en Clientes empresariales: editar la
/// identidad se hace desde la vista rápida, con el lápiz de su cabecera —que está en todas las
/// pestañas— o con la tecla «e» del listado, que llega al panel como una petición de edición de
/// un solo uso. Editar acaba en <c>EditarClienteCommand</c>, así que un rol de Consulta no
/// entra por ninguno de los dos caminos. Mismo contrato que
/// <see cref="EmpresaWorkspacePanelLapizTests"/>.
/// </summary>
public class ClienteWorkspacePanelLapizTests : BunitContext
{
    private const string Lapiz = "button[aria-label='Editar la identidad del Cliente empresarial']";
    private static readonly Guid Id = Guid.NewGuid();

    private MediadorPorFuncion _mediador = null!;
    private int _cargasQueFallan;
    private int _cargasSinFicha;

    private readonly List<string> _pestanasPedidas = [];

    private IRenderedComponent<ClienteWorkspacePanel> Renderizar(string pestana, string rol = Roles.GestorCae)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(rol);
        Services.AddLocalization();
        _mediador = new MediadorPorFuncion(p => p switch
        {
            ObtenerClientePorIdQuery when _cargasQueFallan-- > 0 => throw new InvalidOperationException("Fallo simulado de la carga."),
            ObtenerClientePorIdQuery when _cargasSinFicha-- > 0 => null,
            ObtenerClientePorIdQuery => new ClienteDetalleDto(
                Id, "Refrielectric S.A.", "A-48.220.917", false, null,
                new DateTime(2019, 3, 4, 0, 0, 0, DateTimeKind.Utc), null, Guid.NewGuid()),
            ObtenerResumenClienteQuery => new ResumenClienteDto(
                Id, "Refrielectric S.A.", "A-48.220.917", false,
                new DateTime(2019, 3, 4, 0, 0, 0, DateTimeKind.Utc), null, 3, 42),
            ObtenerEmpresasDeClienteQuery => (IReadOnlyList<EmpresaDeClienteDto>)[],
            ObtenerSubcontratasDeClienteQuery => (IReadOnlyList<SubcontrataDeClienteDto>)[],
            ObtenerCentrosDeClienteQuery => (IReadOnlyList<CentroDeClienteDto>)[],
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {p.GetType().Name}.")
        });
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.TryAddScoped<ContextWorkspaceService>();

        return Render<ClienteWorkspacePanel>(p => p
            .Add(x => x.EntidadId, Id)
            .Add(x => x.PestanaActiva, pestana)
            .Add(x => x.PestanaActivaChanged, EventCallback.Factory.Create<string>(this, _pestanasPedidas.Add)));
    }

    /// <summary>El formulario de identidad está a la vista (solo se pinta en «Información»).</summary>
    private static bool FormularioALaVista(IRenderedComponent<ClienteWorkspacePanel> cut) =>
        cut.FindAll(".workspace-acciones-edicion").Count > 0;

    [Fact]
    public void La_cabecera_lleva_el_lapiz_y_el_icono_360_a_la_pagina_completa()
    {
        var cut = Renderizar("informacion");

        cut.Find(Lapiz).GetAttribute("title").Should().Be("Editar (e)");
        cut.Find(".cabecera-cliente-360 a.boton-360-pagina").GetAttribute("href").Should().Be($"/clientes/{Id}");
    }

    [Fact]
    public async Task El_lapiz_entra_en_edicion_y_desaparece_mientras_se_edita()
    {
        var cut = Renderizar("informacion");
        FormularioALaVista(cut).Should().BeFalse("punto de partida: en lectura");

        await cut.Find(Lapiz).ClickAsync(new MouseEventArgs());

        FormularioALaVista(cut).Should().BeTrue();
        cut.FindAll("input").First().GetAttribute("value").Should().Be("Refrielectric S.A.", "el formulario parte de la identidad vigente");
        cut.FindAll(Lapiz).Should().BeEmpty("ya se está editando");
        _pestanasPedidas.Should().BeEmpty("ya estaba en «Información»");
    }

    /// <summary>El formulario vive en «Información»: desde otra pestaña, el lápiz lleva primero allí.</summary>
    [Fact]
    public async Task Desde_otra_pestana_el_lapiz_lleva_a_Informacion_y_entra_en_edicion()
    {
        var cut = Renderizar("centros");

        await cut.Find(Lapiz).ClickAsync(new MouseEventArgs());

        _pestanasPedidas.Should().Equal(["informacion"]);
        cut.FindAll(Lapiz).Should().BeEmpty("entró en edición");

        // Quien monta el panel atiende la petición de pestaña; el formulario ya estaba preparado.
        cut.Render(p => p.Add(x => x.PestanaActiva, "informacion"));
        FormularioALaVista(cut).Should().BeTrue();
    }

    [Fact]
    public async Task La_peticion_de_edicion_de_la_tecla_e_equivale_a_pulsar_el_lapiz()
    {
        var cut = Renderizar("informacion");
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Cliente, Id, "Refrielectric S.A."));

        cut.WaitForAssertion(() => FormularioALaVista(cut).Should().BeTrue("entró en edición"));
        cut.FindAll(Lapiz).Should().BeEmpty();
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Cliente, Id).Should().BeFalse("el panel la consumió");
    }

    /// <summary>La petición puede llegar antes que el panel: la atiende el final de la carga de su cabecera.</summary>
    [Fact]
    public async Task Una_peticion_que_llego_antes_de_montar_el_panel_se_atiende_al_cargar()
    {
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Cliente, Id, "Refrielectric S.A.");

        var cut = Renderizar("informacion");

        cut.WaitForAssertion(() => FormularioALaVista(cut).Should().BeTrue("entró en edición al terminar de cargar"));
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Cliente, Id).Should().BeFalse("el panel la consumió");
    }

    /// <summary>
    /// Una petición de edición no sobrevive a la carga fallida de su ficha: si se quedara
    /// pendiente, un «Reintentar» con éxito entraría en edición sin que nadie lo pidiera.
    /// </summary>
    [Fact]
    public async Task Si_la_ficha_no_carga_la_peticion_de_edicion_se_descarta_y_Reintentar_no_entra_en_edicion()
    {
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Cliente, Id, "Refrielectric S.A.");
        _cargasQueFallan = 1;

        var cut = Renderizar("informacion");

        cut.WaitForAssertion(() => _mediador.Enviadas.OfType<ObtenerClientePorIdQuery>().Should().ContainSingle(
            "control positivo: la carga se intentó y falló"));
        cut.FindAll(Lapiz).Should().BeEmpty("control positivo: sin detalle no hay cabecera");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll(Lapiz).Should().ContainSingle("control positivo: el reintento cargó la ficha"));
        FormularioALaVista(cut).Should().BeFalse("la carga fallida descartó la petición: nadie pidió editar ahora");
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Cliente, Id).Should().BeFalse();
    }

    /// <summary>Lo mismo si la ficha no existe (o ya no se ve): la consulta no falla, devuelve vacío.</summary>
    [Fact]
    public async Task Si_la_ficha_no_existe_la_peticion_de_edicion_se_descarta()
    {
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Cliente, Id, "Refrielectric S.A.");
        _cargasSinFicha = 1;

        var cut = Renderizar("informacion");

        cut.WaitForAssertion(() => _mediador.Enviadas.OfType<ObtenerClientePorIdQuery>().Should().ContainSingle(
            "control positivo: la carga se intentó"));
        cut.FindAll(Lapiz).Should().BeEmpty("control positivo: sin detalle no hay cabecera");
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Cliente, Id).Should().BeFalse("la ficha inexistente la descartó");
        FormularioALaVista(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Una_peticion_para_otra_ficha_no_pone_esta_en_edicion()
    {
        var cut = Renderizar("informacion");
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        var otra = Guid.NewGuid();

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Cliente, otra, "Montajes Ebro S.L."));

        FormularioALaVista(cut).Should().BeFalse();
        cut.FindAll(Lapiz).Should().ContainSingle();
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Cliente, otra).Should().BeTrue(
            "la petición era de la otra ficha y sigue esperándola: este panel no la consumió");
    }

    /// <summary>
    /// Un rol de Consulta no tiene lápiz, ni el botón «Editar identidad» del cuerpo (misma
    /// puerta: es la misma edición), ni entra en edición con la tecla «e»: la petición llega,
    /// el panel la consume y se queda en lectura.
    /// </summary>
    [Fact]
    public async Task Un_rol_de_Consulta_no_tiene_lapiz_ni_entra_en_edicion_con_la_tecla_e()
    {
        var cut = Renderizar("informacion", Roles.Consulta);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        cut.Find(".cabecera-cliente-360 a.boton-360-pagina").Should().NotBeNull("control positivo: la cabecera está pintada");
        cut.FindAll(Lapiz).Should().BeEmpty();
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Editar identidad");

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Cliente, Id, "Refrielectric S.A."));

        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Cliente, Id).Should().BeFalse(
            "control positivo: la petición llegó al panel, que la consumió sin atenderla");
        FormularioALaVista(cut).Should().BeFalse("editar acaba en un comando que su rol no puede ejecutar");
    }

    /// <summary>Control positivo del caso anterior: con escritura, el cuerpo de «Información» sigue ofreciendo «Editar identidad».</summary>
    [Fact]
    public async Task Con_escritura_el_boton_Editar_identidad_del_cuerpo_sigue_y_hace_lo_mismo_que_el_lapiz()
    {
        var cut = Renderizar("informacion");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Editar identidad").ClickAsync(new MouseEventArgs());

        FormularioALaVista(cut).Should().BeTrue();
        cut.FindAll(Lapiz).Should().BeEmpty();
    }
}
