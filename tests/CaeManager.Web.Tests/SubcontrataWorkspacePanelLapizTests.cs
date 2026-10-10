using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrataSinContrasena;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSupervisionSubcontrata;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Subcontratas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Patrón de listados sin menú «⋯» (decisión 2026-10-08) en Subcontratas: editar se hace desde
/// la vista rápida, con el lápiz de su cabecera —que está en todas las pestañas— o con la tecla
/// «e» del listado, que llega al panel como una petición de edición de un solo uso. Editar
/// carga las credenciales del portal de la Subcontrata, así que un rol de Consulta no entra por
/// ninguno de los dos caminos. Mismo contrato que <see cref="EmpresaWorkspacePanelLapizTests"/>.
/// </summary>
public class SubcontrataWorkspacePanelLapizTests : BunitContext
{
    private const string Lapiz = "button[aria-label='Editar la información de la subcontrata']";
    private static readonly Guid Id = Guid.NewGuid();

    private MediadorPorFuncion _mediador = null!;
    private int _cargasQueFallan;
    private int _cargasSinFicha;

    private readonly List<string> _pestanasPedidas = [];

    private IRenderedComponent<SubcontrataWorkspacePanel> Renderizar(string pestana, string rol = Roles.GestorCae)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(rol);
        Services.AddLocalization();
        _mediador = new MediadorPorFuncion(p => p switch
        {
            ObtenerSubcontrataPorIdQuery when _cargasQueFallan-- > 0 => throw new InvalidOperationException("Fallo simulado de la carga."),
            ObtenerSubcontrataPorIdQuery when _cargasSinFicha-- > 0 => null,
            ObtenerSubcontrataPorIdQuery => new SubcontrataDetalleDto(
                Id, "Pinturas Lauburu S.A.", "A-48.007.615", DateTime.UtcNow, [], [], Guid.NewGuid(), NivelServicioSubcontrata.Gestionada,
                Notas: null, NotaInternaVisible: true),
            ObtenerSubcontratasQuery q => new ResultadoPaginado<SubcontrataListaDto>([], 0, q.Pagina, q.TamanoPagina),
            ObtenerTrabajadoresQuery q => new ResultadoPaginado<TrabajadorListaDto>([], 0, q.Pagina, q.TamanoPagina),
            ObtenerCentrosConActividadDeSubcontrataQuery => (IReadOnlyList<CentroConActividadDto>)[],
            ObtenerSupervisionSubcontrataQuery => new SupervisionSubcontrataDto([], []),
            ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)[],
            ObtenerEmpresasParaSelectorQuery => (IReadOnlyList<EmpresaSelectorDto>)[],
            ObtenerCredencialAccesoSubcontrataSinContrasenaQuery => null,
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {p.GetType().Name}.")
        });
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.TryAddScoped<ContextWorkspaceService>();

        return Render<SubcontrataWorkspacePanel>(p => p
            .Add(x => x.EntidadId, Id)
            .Add(x => x.PestanaActiva, pestana)
            .Add(x => x.PestanaActivaChanged, EventCallback.Factory.Create<string>(this, _pestanasPedidas.Add)));
    }

    private bool EntroEnEdicion => _mediador.Enviadas.OfType<ObtenerCredencialAccesoSubcontrataSinContrasenaQuery>().Any();

    [Fact]
    public void La_cabecera_lleva_el_lapiz_y_el_icono_360_a_la_pagina_completa()
    {
        var cut = Renderizar("informacion");

        cut.Find(Lapiz).GetAttribute("title").Should().Be("Editar (e)");
        cut.Find("a.boton-360-pagina").GetAttribute("href").Should().Be($"/subcontratas/{Id}");
    }

    [Fact]
    public async Task El_lapiz_entra_en_edicion_y_desaparece_mientras_se_edita()
    {
        var cut = Renderizar("informacion");
        EntroEnEdicion.Should().BeFalse("punto de partida: en lectura");

        await cut.Find(Lapiz).ClickAsync(new MouseEventArgs());

        EntroEnEdicion.Should().BeTrue();
        cut.FindAll(Lapiz).Should().BeEmpty("ya se está editando");
        _pestanasPedidas.Should().BeEmpty("ya estaba en «Información»");
    }

    /// <summary>El formulario vive en «Información»: desde otra pestaña, el lápiz lleva primero allí.</summary>
    [Fact]
    public async Task Desde_otra_pestana_el_lapiz_lleva_a_Informacion_y_entra_en_edicion()
    {
        var cut = Renderizar("supervision");

        await cut.Find(Lapiz).ClickAsync(new MouseEventArgs());

        _pestanasPedidas.Should().Equal(["informacion"]);
        EntroEnEdicion.Should().BeTrue();
    }

    [Fact]
    public async Task La_peticion_de_edicion_de_la_tecla_e_equivale_a_pulsar_el_lapiz()
    {
        var cut = Renderizar("informacion");
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Subcontrata, Id, "Pinturas Lauburu S.A."));

        cut.WaitForAssertion(() => cut.FindAll(Lapiz).Should().BeEmpty("entró en edición"));
        EntroEnEdicion.Should().BeTrue();
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Subcontrata, Id).Should().BeFalse("el panel la consumió");
    }

    /// <summary>La petición puede llegar antes que el panel: la atiende el final de su cadena de cargas.</summary>
    [Fact]
    public async Task Una_peticion_que_llego_antes_de_montar_el_panel_se_atiende_al_cargar()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Subcontrata, Id, "Pinturas Lauburu S.A.");

        var cut = Renderizar("informacion");

        cut.WaitForAssertion(() => cut.FindAll(Lapiz).Should().BeEmpty("entró en edición al terminar de cargar"));
        EntroEnEdicion.Should().BeTrue();
    }

    /// <summary>
    /// Una petición de edición no sobrevive a la carga fallida de su ficha: si se quedara
    /// pendiente, un reintento con éxito entraría en edición sin que nadie lo pidiera.
    /// </summary>
    [Fact]
    public async Task Si_la_ficha_no_carga_la_peticion_de_edicion_se_descarta()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Subcontrata, Id, "Pinturas Lauburu S.A.");
        _cargasQueFallan = 1;

        var cut = Renderizar("informacion");

        cut.WaitForAssertion(() => _mediador.Enviadas.OfType<ObtenerSubcontrataPorIdQuery>().Should().ContainSingle(
            "control positivo: la carga se intentó y falló"));
        cut.FindAll(Lapiz).Should().BeEmpty("control positivo: sin detalle no hay cabecera");
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Subcontrata, Id).Should().BeFalse("la carga fallida la descartó");
        EntroEnEdicion.Should().BeFalse();
    }

    /// <summary>Lo mismo si la ficha no existe (o ya no se ve): la consulta no falla, devuelve vacío.</summary>
    [Fact]
    public async Task Si_la_ficha_no_existe_la_peticion_de_edicion_se_descarta()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var workspace = new ContextWorkspaceService();
        Services.AddScoped(_ => workspace);
        await workspace.AbrirEnEdicionAsync(EntidadWorkspace.Subcontrata, Id, "Pinturas Lauburu S.A.");
        _cargasSinFicha = 1;

        var cut = Renderizar("informacion");

        cut.WaitForAssertion(() => _mediador.Enviadas.OfType<ObtenerSubcontrataPorIdQuery>().Should().ContainSingle(
            "control positivo: la carga se intentó"));
        cut.FindAll(Lapiz).Should().BeEmpty("control positivo: sin detalle no hay cabecera");
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Subcontrata, Id).Should().BeFalse("la ficha inexistente la descartó");
        EntroEnEdicion.Should().BeFalse();
    }

    [Fact]
    public async Task Una_peticion_para_otra_ficha_no_pone_esta_en_edicion()
    {
        var cut = Renderizar("informacion");
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Subcontrata, Guid.NewGuid(), "Andamios Bidasoa S.L."));

        EntroEnEdicion.Should().BeFalse();
        cut.FindAll(Lapiz).Should().ContainSingle();
    }

    [Fact]
    public async Task Un_rol_de_Consulta_no_tiene_lapiz_ni_entra_en_edicion_con_la_tecla_e()
    {
        var cut = Renderizar("informacion", Roles.Consulta);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        cut.Find("a.boton-360-pagina").Should().NotBeNull("control positivo: la cabecera está pintada");
        cut.FindAll(Lapiz).Should().BeEmpty();

        await cut.InvokeAsync(() => workspace.AbrirEnEdicionAsync(EntidadWorkspace.Subcontrata, Id, "Pinturas Lauburu S.A."));

        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Subcontrata, Id).Should().BeFalse(
            "control positivo: la petición llegó al panel, que la consumió sin atenderla");
        EntroEnEdicion.Should().BeFalse("editar es la puerta a las credenciales del portal de la Subcontrata");
    }
}
