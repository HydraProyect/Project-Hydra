using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentacionBaseTrabajadores;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Features.Trabajadores.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Ver» el documento y su enlace a la ficha 360 (<c>/documentos/{id}</c>) en las piezas compartidas de las fichas 360 de
/// Trabajador (Documentación base) y de Empresa (pestaña Documentación): lo que ya había —«Gestionar» por rol y el panel de
/// 520 px— se conserva, y los hosts que no piden «Ver» (cajón, panel) no cambian.
/// </summary>
public class Ficha360VerDocumentoPanelTests : BunitContext
{
    private static readonly Guid Vencido = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Vigente = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    private sealed class MediadorQueNoSeUsa : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    public Ficha360VerDocumentoPanelTests()
    {
        Services.AddLocalization();
        Services.AddSingleton<IMediator>(new MediadorQueNoSeUsa());
    }

    private static DocumentacionBaseTrabajadorDto Base() => new(
    [
        new(TipoDocumentoBase.AptitudMedica, EstadoIndicadorBase.Vencido, Vencido, null),
        new(TipoDocumentoBase.FormacionArt19, EstadoIndicadorBase.Falta, null, null),
        new(TipoDocumentoBase.InformacionArt18, EstadoIndicadorBase.Vigente, Vigente, null),
    ]);

    private IRenderedComponent<PanelDocumentacionBase> Panel(List<(Guid, string)> vistos, List<Guid>? corregidos = null) => Render<PanelDocumentacionBase>(p =>
    {
        p.Add(c => c.Datos, Base())
         .Add(c => c.OnVerDocumento, EventCallback.Factory.Create<(Guid DocumentoId, string Titulo)>(this, d => vistos.Add((d.DocumentoId, d.Titulo))));
        if (corregidos is not null)
            p.Add(c => c.OnCorregirDocumento, EventCallback.Factory.Create<Guid>(this, corregidos.Add));
    });

    private static List<string> Botones(AngleSharp.Dom.IElement fila) => fila.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList();

    [Fact]
    public void El_nombre_de_cada_indicador_con_documento_enlaza_a_su_ficha_360_y_el_que_falta_es_texto()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var cut = Panel([]);

        var filas = cut.FindAll(".panel-doc-base-fila");
        filas[0].QuerySelector(".panel-doc-base-nombre a")!.GetAttribute("href").Should().Be($"/documentos/{Vencido}");
        filas[0].QuerySelector(".panel-doc-base-nombre a")!.TextContent.Should().Be("Aptitud médica");
        filas[2].QuerySelector(".panel-doc-base-nombre a")!.GetAttribute("href").Should().Be($"/documentos/{Vigente}");
        filas[1].QuerySelector("a").Should().BeNull();
        filas[1].TextContent.Should().Contain("Formación Art. 19");
    }

    [Fact]
    public async Task Ver_avisa_con_el_DocumentoId_y_el_titulo_de_su_fila_y_no_dispara_Gestionar()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var vistos = new List<(Guid, string)>();
        var corregidos = new List<Guid>();
        var cut = Panel(vistos, corregidos);

        await cut.FindAll(".panel-doc-base-fila")[2].QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Ver")
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll(".panel-doc-base-fila")[0].QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Ver")
            .ClickAsync(new MouseEventArgs());

        vistos.Should().Equal([(Vigente, "Información Art. 18"), (Vencido, "Aptitud médica")]);
        corregidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Gestionar_se_conserva_en_su_fila_y_sigue_abriendo_el_editor_del_documento()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var vistos = new List<(Guid, string)>();
        var corregidos = new List<Guid>();
        var cut = Panel(vistos, corregidos);

        cut.FindAll(".panel-doc-base-fila").Select(Botones).Should().Equal(
            new List<string>[] { ["Ver", "Gestionar"], [], ["Ver"] }, (a, b) => a.SequenceEqual(b));

        await cut.FindAll(".panel-doc-base-fila")[0].QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Gestionar")
            .ClickAsync(new MouseEventArgs());

        corregidos.Should().Equal(Vencido);
        vistos.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public void Ver_es_para_todo_rol_que_ve_la_ficha_y_Consulta_y_Cliente_no_reciben_Gestionar(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Panel([], []);

        cut.FindAll(".panel-doc-base-fila").Select(Botones).Should().Equal(
            new List<string>[] { ["Ver"], [], ["Ver"] }, (a, b) => a.SequenceEqual(b));
        cut.Markup.Should().NotContain("Gestionar");
    }

    [Fact]
    public void Ver_solo_sale_en_el_indicador_cuyo_Documento_tiene_archivo_y_el_enlace_al_documento_se_conserva()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var cut = Render<PanelDocumentacionBase>(p => p
            .Add(c => c.Datos, Base())
            .Add(c => c.OnVerDocumento, EventCallback.Factory.Create<(Guid DocumentoId, string Titulo)>(this, _ => { }))
            .Add(c => c.OnCorregirDocumento, EventCallback.Factory.Create<Guid>(this, _ => { }))
            .Add(c => c.DocumentosConArchivo, new HashSet<Guid> { Vigente }));

        var filas = cut.FindAll(".panel-doc-base-fila");
        // El Documento vencido no tiene archivo en la lista de la ficha: sin «Ver», pero con enlace y «Gestionar».
        Botones(filas[0]).Should().Equal("Gestionar");
        filas[0].QuerySelector(".panel-doc-base-nombre a")!.GetAttribute("href").Should().Be($"/documentos/{Vencido}");
        Botones(filas[2]).Should().Equal("Ver");
    }

    [Fact]
    public void Ver_lleva_el_nombre_del_documento_en_su_etiqueta_accesible()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var cut = Panel([]);

        var ver = cut.FindAll(".panel-doc-base-fila")[0].QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Ver");
        ver.GetAttribute("aria-label").Should().Be("Ver el PDF del documento «Aptitud médica»");
    }

    [Fact]
    public void Sin_OnVerDocumento_el_cajon_y_el_panel_no_pintan_ni_enlace_ni_Ver()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var cut = Render<PanelDocumentacionBase>(p => p
            .Add(c => c.Datos, Base())
            .Add(c => c.OnCorregirDocumento, EventCallback.Factory.Create<Guid>(this, _ => { })));

        cut.FindAll(".panel-doc-base-nombre a").Should().BeEmpty();
        cut.FindAll(".panel-doc-base-fila").Select(Botones).Should().Equal(
            new List<string>[] { ["Gestionar"], [], [] }, (a, b) => a.SequenceEqual(b));
    }

    // ---- Empresa 360 › Documentación (PestanaDocumentacion con ConVerDocumento) ----

    private sealed class MediatorDocumentos(IReadOnlyList<DocumentoListaDto> documentos) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerDocumentosQuery => new ResultadoPaginado<DocumentoListaDto>(documentos, documentos.Count, 1, 50),
                _ => throw new NotSupportedException(request.GetType().Name)
            }));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private static readonly DocumentoListaDto ConArchivo = new(Guid.Parse("dddddddd-0000-0000-0000-000000000001"), AmbitoAplicacion.Empresa,
        "Ibertec GmbH", "Seguro de responsabilidad civil", new(2025, 1, 1), new(2026, 11, 6), EstadoDocumento.Vencido, "empresas/seguro.pdf", []);
    private static readonly DocumentoListaDto SinArchivo = new(Guid.Parse("dddddddd-0000-0000-0000-000000000002"), AmbitoAplicacion.Empresa,
        "Ibertec GmbH", "Certificado de la Seguridad Social", new(2025, 1, 1), new(2027, 1, 1), EstadoDocumento.Vigente, null, []);

    private IRenderedComponent<PestanaDocumentacion> EmpresaDocumentacion(bool conVer, string rol = Roles.GestorCae)
    {
        this.ConRolDeEscritura(rol);
        Services.AddScoped<IMediator>(_ => new MediatorDocumentos([ConArchivo, SinArchivo]));
        Services.AddScoped<CaeManager.Web.Components.DesignSystem.ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        ComponentFactories.AddStub<VisorDocumento>();
        var cut = Render<PestanaDocumentacion>(p => p
            .Add(x => x.Ambito, AmbitoAplicacion.Empresa).Add(x => x.PropietarioId, Guid.NewGuid())
            .Add(x => x.EnPagina, true).Add(x => x.ConFiltros, true).Add(x => x.ConVerDocumento, conVer));
        cut.WaitForAssertion(() => cut.FindAll("li.fila-relacion").Should().HaveCount(2));
        return cut;
    }

    private static List<string> BotonesDeAcciones(AngleSharp.Dom.IElement fila) =>
        fila.QuerySelectorAll(".fila-relacion-acciones button").Select(b => b.TextContent.Trim()).ToList();

    [Fact]
    public void Empresa_enlaza_el_nombre_del_documento_a_su_ficha_360_y_ofrece_Ver_solo_con_archivo_y_Gestionar_donde_pide_trabajo()
    {
        var cut = EmpresaDocumentacion(conVer: true);

        var filas = cut.FindAll("li.fila-relacion");
        filas[0].QuerySelector("a.fila-relacion-nombre")!.GetAttribute("href").Should().Be($"/documentos/{ConArchivo.Id}");
        filas[1].QuerySelector("a.fila-relacion-nombre")!.GetAttribute("href").Should().Be($"/documentos/{SinArchivo.Id}");
        // «360» es el botón del panel de 520 px que antes abría el propio nombre; va aparte de «Ver» y «Gestionar».
        BotonesDeAcciones(filas[0]).Should().Contain(["Ver", "Gestionar"]);
        // Vigente también lleva «Gestionar» discreto en esta pestaña (como en main); sin archivo no hay nada que ver.
        BotonesDeAcciones(filas[1]).Should().NotContain("Ver").And.Contain("Gestionar");
        filas[0].QuerySelectorAll(".fila-relacion-acciones button").Single(b => b.TextContent.Trim() == "Ver")
            .GetAttribute("aria-label").Should().Be("Ver el PDF del documento «Seguro de responsabilidad civil»");
    }

    [Fact]
    public async Task Empresa_Ver_abre_el_visor_con_el_DocumentoId_de_la_fila_y_el_boton_360_conserva_el_panel_del_documento()
    {
        var cut = EmpresaDocumentacion(conVer: true);

        await cut.FindAll("li.fila-relacion")[0].QuerySelectorAll(".fila-relacion-acciones button").Single(b => b.TextContent.Trim() == "Ver")
            .ClickAsync(new MouseEventArgs());

        var visor = cut.FindComponent<Stub<VisorDocumento>>();
        visor.Instance.Parameters.Get(x => x.Visible).Should().BeTrue();
        visor.Instance.Parameters.Get(x => x.DocumentoId).Should().Be(ConArchivo.Id);
        visor.Instance.Parameters.Get(x => x.Titulo).Should().Be("Seguro de responsabilidad civil");

        await cut.FindAll("li.fila-relacion")[1].QuerySelector(".fila-relacion-acciones button.boton-360")!
            .ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual!.EntidadId.Should().Be(SinArchivo.Id);
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public void Empresa_Consulta_y_Cliente_ven_Ver_pero_no_Gestionar(string rol)
    {
        var cut = EmpresaDocumentacion(conVer: true, rol);

        var filas = cut.FindAll("li.fila-relacion");
        BotonesDeAcciones(filas[0]).Should().Contain("Ver").And.NotContain("Gestionar");
    }

    [Fact]
    public async Task Sin_ConVerDocumento_Proyecto_y_el_resto_de_hosts_conservan_el_nombre_que_abre_el_panel_y_no_tienen_Ver()
    {
        var cut = EmpresaDocumentacion(conVer: false);

        cut.FindAll("li.fila-relacion a.fila-relacion-nombre").Should().BeEmpty();
        cut.FindAll(".fila-relacion-acciones button").Select(b => b.TextContent.Trim()).Should().NotContain("Ver");
        await cut.FindAll("li.fila-relacion .fila-relacion-nombre")[0].ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual!.EntidadId.Should().Be(ConArchivo.Id);
    }
}
