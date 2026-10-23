using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Proyectos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Proyectos.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class ProyectosGen2Tests
{
    [Fact]
    public async Task El_chevron_carga_solo_al_desplegar_y_no_abre_la_vista_rapida()
    {
        Services.AddScoped<ContextWorkspaceService>();
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        _mediator.Enviados.OfType<ObtenerDocumentosQuery>().Should().BeEmpty();
        var chevron = cut.Find(".proyecto-expandir");
        chevron.GetAttribute("aria-expanded").Should().Be("false");
        await chevron.ClickAsync(new());
        cut.Find(".proyecto-expandir").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("tr.proyecto-documentacion").Should().ContainSingle();
        cut.FindAll("aside.panel-proyecto").Should().BeEmpty();
        _mediator.Enviados.OfType<ObtenerDocumentosQuery>().Should().ContainSingle(q => q.PropietarioId == AbiertoId);
        await cut.Find(".proyecto-expandir").ClickAsync(new());
        cut.FindAll("tr.proyecto-documentacion").Should().BeEmpty();
    }

    [Fact]
    public async Task Expandir_todo_despliega_y_contrae_la_documentacion_de_todas_las_filas()
    {
        Services.AddScoped<ContextWorkspaceService>();
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        cut.FindAll("tr.proyecto-documentacion").Should().BeEmpty();

        await cut.FindAll(".barra-herramientas-lista button").Single(b => b.TextContent.Contains("Expandir todo")).ClickAsync(new());
        cut.FindAll("tr.proyecto-documentacion").Should().HaveCount(2);

        await cut.FindAll(".barra-herramientas-lista button").Single(b => b.TextContent.Contains("Contraer todo")).ClickAsync(new());
        cut.FindAll("tr.proyecto-documentacion").Should().BeEmpty();
    }

    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, true)]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task El_listado_conserva_las_acciones_de_main_por_rol(string rol, bool escribe)
    {
        this.ConRolDeEscritura(rol);
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        cut.FindAll("tbody a.boton-360-pagina").Should().HaveCount(2);
        cut.FindAll("header a[data-keytip=X]").Should().ContainSingle();
        cut.FindAll("header button").Any(b => b.TextContent.Trim() == "+ Nuevo proyecto").Should().Be(escribe);
        FilaDe(cut, ProyectoCerrado).QuerySelectorAll(".proyectos-accion-rapida").Any().Should().Be(escribe);
        await AbrirDetalle(cut, ProyectoAbierto);
        cut.FindAll("aside .pie-panel-proyecto button").Select(b => b.TextContent.Trim())
            .Should().Equal(escribe ? ["Cerrar proyecto", "Eliminar"] : Array.Empty<string>());
        cut.FindAll("aside button[aria-label='Editar la información del proyecto']").Any().Should().Be(escribe);
        cut.FindAll("aside a.boton-360-pagina").Should().ContainSingle();
        cut.Markup.Should().Contain("Información").And.Contain("Técnicos").And.Contain("Documentos");
    }

    [Fact]
    public async Task Agrupar_pide_todas_las_filas_en_servidor_y_nace_plegado_conservando_las_filas_de_Evento()
    {
        _mediator.Proyectos = [ProyectoAbierto with { Tipo = TipoProyecto.Evento }];
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = Renderizar();
        var filtro = cut.FindComponents<PastillaFiltro>().Single(c => c.Instance.Etiqueta == "Agrupar");
        await cut.InvokeAsync(() => filtro.Instance.ValorChanged.InvokeAsync("empresa"));
        cut.FindAll("tr.proyectos-grupo .grupo-lista-nombre").Select(f => f.TextContent.Trim())
            .Should().BeEquivalentTo("Refrielectric S.L.", "Frigoríficos Arcos S.A.");
        var consulta = _mediator.Enviados.OfType<ObtenerProyectosQuery>().Last();
        consulta.AgruparPorEmpresa.Should().BeTrue();
        consulta.TamanoPagina.Should().Be(AgrupacionDeLista.TamanoPaginaAgrupada, "los grupos se calculan sobre todas las filas filtradas");
        cut.FindAll("tbody .nombre-proyecto").Should().BeEmpty("todo nace plegado");
        cut.FindAll("tr.proyectos-grupo button.grupo-lista-cabecera").Should().OnlyContain(b => b.GetAttribute("aria-expanded") == "false");

        await AlternarGrupoDeClienteAsync(cut, "Refrielectric S.L.");
        cut.FindAll("tbody .nombre-proyecto").Should().ContainSingle();
        cut.Markup.Should().Contain("Evento");
        await AlternarGrupoDeClienteAsync(cut, "Frigoríficos Arcos S.A.");
        cut.FindAll("tbody .nombre-proyecto").Should().HaveCount(2);
        await AlternarGrupoDeClienteAsync(cut, "Refrielectric S.L.");
        cut.FindAll("tbody .nombre-proyecto").Should().ContainSingle("plegar un grupo oculta sus filas");

        await cut.InvokeAsync(() => filtro.Instance.ValorChanged.InvokeAsync(string.Empty));
        cut.FindAll("tr.proyectos-grupo").Should().BeEmpty();
        cut.FindAll("tbody .nombre-proyecto").Should().HaveCount(2);
    }

    [Fact]
    public async Task El_grupo_plegado_dice_cuantos_proyectos_estan_en_rojo_y_abierto_no()
    {
        _mediator.Proyectos = [ProyectoAbierto with { EstadoDocumental = EstadoDocumento.Vencido }, ProyectoAbierto2 with { EstadoDocumental = EstadoDocumento.Vigente }];
        var cut = Renderizar();
        var filtro = cut.FindComponents<PastillaFiltro>().Single(c => c.Instance.Etiqueta == "Agrupar");
        await cut.InvokeAsync(() => filtro.Instance.ValorChanged.InvokeAsync("empresa"));
        cut.Find("tr.proyectos-grupo .grupo-lista-contador").TextContent.Trim().Should().Be("· 2");
        cut.Find("tr.proyectos-grupo .grupo-lista-incidencias").TextContent.Should().Contain("1 con incidencia");

        await AlternarGrupoDeClienteAsync(cut, "Refrielectric S.L.");
        cut.FindAll("tr.proyectos-grupo .grupo-lista-incidencias").Should().BeEmpty("abierto, las filas rojas ya se ven");
    }

    [Fact]
    public async Task Expandir_todo_solo_alcanza_las_filas_de_los_grupos_abiertos()
    {
        Services.AddScoped<ContextWorkspaceService>();
        _mediator.Proyectos = [ProyectoAbierto];
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = Renderizar();
        var filtro = cut.FindComponents<PastillaFiltro>().Single(c => c.Instance.Etiqueta == "Agrupar");
        await cut.InvokeAsync(() => filtro.Instance.ValorChanged.InvokeAsync("empresa"));
        await AlternarGrupoDeClienteAsync(cut, "Refrielectric S.L.");

        await cut.FindAll(".barra-herramientas-lista button").Single(b => b.TextContent.Contains("Expandir todo")).ClickAsync(new());

        cut.FindAll("tr.proyecto-documentacion").Should().ContainSingle("la fila del grupo plegado no se ve y no se expande");
        _mediator.Enviados.OfType<ObtenerDocumentosQuery>().Should().ContainSingle(q => q.PropietarioId == AbiertoId);
    }

    private static async Task AlternarGrupoDeClienteAsync(IRenderedComponent<Proyectos> cut, string cliente) =>
        await cut.FindAll("tr.proyectos-grupo").Single(g => g.QuerySelector(".grupo-lista-nombre")!.TextContent.Trim() == cliente)
            .QuerySelector("button.grupo-lista-cabecera")!.ClickAsync(new());
    private static DocumentoListaDto DocumentoVigente() => new(Guid.NewGuid(), AmbitoAplicacion.Proyecto,
        "Proyecto", "Evaluación de riesgos", new DateOnly(2026, 10, 1), new DateOnly(2027, 10, 1), EstadoDocumento.Vigente, null, []);

    private IRenderedComponent<DocumentacionFilaProyecto> RenderizarDocumentacion()
    {
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ContextWorkspaceService>();
        return Render<DocumentacionFilaProyecto>(p => p.Add(c => c.ProyectoId, AbiertoId));
    }

    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, true)]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task La_documentacion_es_legible_por_Consulta_y_solo_ofrece_Gestionar_con_escritura(string rol, bool escribe)
    {
        this.ConRolDeEscritura(rol);
        var documento = DocumentoVigente();
        _mediator.Documentos = [documento];
        var cut = RenderizarDocumentacion();
        var consulta = _mediator.Enviados.OfType<ObtenerDocumentosQuery>().Single();
        consulta.PropietarioId.Should().Be(AbiertoId);
        consulta.Ambito.Should().Be(AmbitoAplicacion.Proyecto);
        cut.Find(".badge-neutro").TextContent.Should().Be("Vigente");
        cut.FindAll("button").Any(b => b.TextContent.Trim() == "Gestionar").Should().Be(escribe);
        var contexto = Services.GetRequiredService<ContextWorkspaceService>();
        contexto.EstaAbierto.Should().BeFalse();
        await cut.Find(".enlace-nombre-fila").ClickAsync(new());
        contexto.FrameActual!.EntidadId.Should().Be(documento.Id);
    }

    [Fact]
    public async Task La_documentacion_distingue_error_y_vacio_y_permite_reintentar()
    {
        _mediator.FallarDocumentacion = true;
        var cut = RenderizarDocumentacion();
        cut.Markup.Should().Contain("No pudimos cargar la documentación");
        _mediator.FallarDocumentacion = false;
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").ClickAsync(new());
        cut.Markup.Should().Contain("Sin documentos registrados").And.NotContain("Todo lo exigido está al día");
    }

    [Fact]
    public async Task La_documentacion_ensena_los_diez_primeros_y_Ver_los_N_restantes_despliega_el_resto()
    {
        _mediator.Documentos = Enumerable.Range(0, 12).Select(_ => DocumentoVigente()).ToList();
        var cut = RenderizarDocumentacion();
        cut.FindAll(".documento-proyecto").Should().HaveCount(10);

        await cut.Find("button[data-accion=ver-restantes]").ClickAsync(new());

        cut.FindAll(".documento-proyecto").Should().HaveCount(12);
        cut.FindAll("button[data-accion=ver-restantes]").Should().BeEmpty();
    }
}
