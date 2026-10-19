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
    public async Task Agrupar_pide_el_orden_en_servidor_y_conserva_las_filas_de_Evento()
    {
        _mediator.Proyectos = [ProyectoAbierto with { Tipo = TipoProyecto.Evento }];
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = Renderizar();
        var filtro = cut.FindComponents<PastillaFiltro>().Single(c => c.Instance.Etiqueta == "Agrupar");
        await cut.InvokeAsync(() => filtro.Instance.ValorChanged.InvokeAsync("empresa"));
        cut.FindAll("tr.proyectos-grupo").Select(f => f.TextContent.Trim())
            .Should().BeEquivalentTo("Refrielectric S.L.", "Frigoríficos Arcos S.A.");
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Last().AgruparPorEmpresa.Should().BeTrue();
        cut.FindAll("tbody .nombre-proyecto").Should().HaveCount(2);
        cut.Markup.Should().Contain("Evento");
        await cut.InvokeAsync(() => filtro.Instance.ValorChanged.InvokeAsync(string.Empty));
        cut.FindAll("tr.proyectos-grupo").Should().BeEmpty();
    }

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
}
