using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pasada visual de staging (2026-10-17): Empresa 360 › Documentación pintaba una lista con viñetas de disco. En página 360
/// (<c>EnPagina</c>) usa la gramática de los documentos de Trabajador 360 (tarjeta, fila con vigencia y pastilla de estado,
/// sin viñetas); el panel de 520 px conserva la lista de filas-botón.
/// </summary>
public class PestanaDocumentacionEnPaginaTests : BunitContext
{
    [Theory]
    [InlineData("GestorCae", EstadoDocumento.Vencido, "Venció el 06/11/2026", true)]
    [InlineData("GestorCae", EstadoDocumento.Proximo, "Caduca el 06/11/2026", true)]
    [InlineData("GestorCae", EstadoDocumento.SinConfirmar, "Falta la fecha de fin", true)]
    [InlineData("GestorCae", EstadoDocumento.Vigente, "Caduca el 06/11/2026", true)]
    [InlineData("GestorCae", EstadoDocumento.SinCaducidad, "Sin caducidad", true)]
    [InlineData("Consulta", EstadoDocumento.Vencido, "Venció el 06/11/2026", false)]
    [InlineData("Consulta", EstadoDocumento.SinConfirmar, "Falta la fecha de fin", false)]
    public async Task Empresa_conserva_la_fila_compacta_y_la_consulta_del_documento_por_rol(
        string rol, EstadoDocumento estado, string vigencia, bool gestionar)
    {
        this.ConRolDeEscritura(rol);
        var documento = new DocumentoListaDto(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH",
            "Contrato de trabajo", new(2025, 1, 1), new(2026, 11, 6), estado, null, []);
        Services.AddScoped<IMediator>(_ => new MediatorFalso([documento]));
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddLocalization();
        var cut = Render<PestanaDocumentacion>(p => p.Add(x => x.Ambito, AmbitoAplicacion.Empresa)
            .Add(x => x.PropietarioId, Guid.NewGuid()).Add(x => x.EnPagina, true));
        var fila = cut.Find(".fila-relacion-compacta");
        fila.Children.Should().HaveCount(4);
        fila.QuerySelector(".fila-relacion-cifra")!.TextContent.Should().Be(vigencia);
        var acciones = fila.QuerySelectorAll(".fila-relacion-acciones button");
        acciones.Should().HaveCount(gestionar ? 1 : 0);
        if (gestionar) acciones[0].TextContent.Trim().Should().Be("Gestionar");
        await cut.Find(".fila-relacion-nombre").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual!.EntidadId.Should().Be(documento.Id);
    }

    private sealed class MediatorFalso(IReadOnlyList<DocumentoListaDto> documentos) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerDocumentosQuery => new ResultadoPaginado<DocumentoListaDto>(documentos, documentos.Count, 1, 50),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private IRenderedComponent<PestanaDocumentacion> Renderizar(bool enPagina)
    {
        this.ConRolDeEscritura();
        var documentos = new List<DocumentoListaDto>
        {
            new(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH", "Seguro de responsabilidad civil",
                new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31), EstadoDocumento.Vigente, null, []),
            new(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH", "Certificado de la Seguridad Social",
                new DateOnly(2025, 1, 1), null, EstadoDocumento.Faltante, null, []),
        };
        Services.AddScoped<IMediator>(_ => new MediatorFalso(documentos));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddLocalization();

        var cut = Render<PestanaDocumentacion>(p => p
            .Add(x => x.Ambito, AmbitoAplicacion.Empresa)
            .Add(x => x.PropietarioId, Guid.NewGuid())
            .Add(x => x.EnPagina, enPagina));
        cut.WaitForAssertion(() => cut.FindAll("li").Should().HaveCount(2));
        return cut;
    }

    /// <summary>
    /// Maqueta «Empresa 360» (B6.1): la pestaña Documentación lleva un chip por estado con su recuento y el buscador. El
    /// filtro es en memoria; Urgente y Próximo se leen los dos «Por vencer» y suman en un solo chip. Sin <c>ConFiltros</c>
    /// (otras fichas) la barra no existe.
    /// </summary>
    [Fact]
    public async Task Con_filtros_hay_un_chip_por_estado_con_recuento_y_el_buscador_acota_la_lista()
    {
        this.ConRolDeEscritura();
        var documentos = new List<DocumentoListaDto>
        {
            new(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH", "Seguro de responsabilidad civil", new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31), EstadoDocumento.Vigente, null, []),
            new(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH", "Certificado de la Seguridad Social", new DateOnly(2025, 1, 1), null, EstadoDocumento.Faltante, null, []),
            new(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH", "Evaluación de riesgos", new DateOnly(2025, 1, 1), new DateOnly(2026, 11, 20), EstadoDocumento.Urgente, null, []),
            new(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH", "Memoria preventiva", new DateOnly(2025, 1, 1), new DateOnly(2027, 1, 20), EstadoDocumento.Proximo, null, []),
        };
        Services.AddScoped<IMediator>(_ => new MediatorFalso(documentos));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddLocalization();
        var cut = Render<PestanaDocumentacion>(p => p
            .Add(x => x.Ambito, AmbitoAplicacion.Empresa).Add(x => x.PropietarioId, Guid.NewGuid()).Add(x => x.EnPagina, true)
            .Add(x => x.ConFiltros, true).Add(x => x.PlaceholderBuscador, "Buscar documento de esta empresa…"));
        cut.WaitForAssertion(() => cut.FindAll("li.fila-relacion").Should().HaveCount(4));

        var chips = cut.FindAll(".documentacion-chip").Select(c => c.TextContent.Trim()).ToList();
        chips.Should().Equal("Todos · 4", "Pendiente · 1", "Por vencer · 2", "Vigente · 1");

        await cut.FindAll(".documentacion-chip").Single(c => c.TextContent.Contains("Por vencer")).ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.FindAll("li.fila-relacion .fila-relacion-nombre").Select(n => n.TextContent.Trim())
            .Should().BeEquivalentTo("Evaluación de riesgos", "Memoria preventiva");

        await cut.FindAll(".documentacion-chip").First().ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        // CampoTexto notifica tras un debounce real de 300 ms (Task.Delay). Con Input() + WaitForAssertion (1 s por
        // defecto) el test competía contra ese temporizador: en un runner cargado la notificación llegaba tarde y
        // fallaba con «Check count: 1» (sin ningún render entre medias, issue #2065). InputAsync espera a que el
        // manejador termine, incluida la notificación al padre y su render, así que la aserción no depende del reloj.
        await cut.Find(".buscador-lista input[type=search]").InputAsync("seguridad");
        cut.FindAll("li.fila-relacion .fila-relacion-nombre").Select(n => n.TextContent.Trim())
            .Should().Equal("Certificado de la Seguridad Social");
    }

    /// <summary>
    /// Si al recargar desaparece el estado filtrado (p. ej. se corrige la única incidencia), el filtro vuelve a «Todos»:
    /// sin eso la lista quedaba vacía y ningún chip aparecía activo.
    /// </summary>
    [Fact]
    public async Task Con_filtros_si_tras_recargar_ya_no_hay_documentos_del_estado_filtrado_vuelve_a_Todos()
    {
        this.ConRolDeEscritura();
        var documentos = new List<DocumentoListaDto>
        {
            new(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH", "Seguro de responsabilidad civil", new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31), EstadoDocumento.Vigente, null, []),
            new(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH", "Certificado de la Seguridad Social", new DateOnly(2025, 1, 1), null, EstadoDocumento.Faltante, null, []),
        };
        Services.AddScoped<IMediator>(_ => new MediatorFalso(documentos));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddLocalization();
        var propietario = Guid.NewGuid();
        var cut = Render<PestanaDocumentacion>(p => p
            .Add(x => x.Ambito, AmbitoAplicacion.Empresa).Add(x => x.PropietarioId, propietario).Add(x => x.EnPagina, true)
            .Add(x => x.ConFiltros, true));
        cut.WaitForAssertion(() => cut.FindAll("li.fila-relacion").Should().HaveCount(2));

        await cut.FindAll(".documentacion-chip").Single(c => c.TextContent.Contains("Pendiente")).ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.FindAll("li.fila-relacion").Should().ContainSingle();

        documentos.RemoveAt(1);
        cut.Render(p => p.Add(x => x.PropietarioId, Guid.NewGuid()));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("li.fila-relacion").Should().ContainSingle();
            cut.FindAll(".documentacion-chip").First().GetAttribute("aria-pressed").Should().Be("true");
        });
    }

    [Fact]
    public void Sin_filtros_la_pestana_no_pinta_barra_de_chips_ni_buscador()
    {
        var cut = Renderizar(enPagina: true);

        cut.FindAll(".documentacion-barra").Should().BeEmpty();
        cut.FindAll(".buscador-lista").Should().BeEmpty();
    }
    [Fact]
    public void En_pagina_los_documentos_son_filas_de_una_tarjeta_con_vigencia_y_pastilla_de_estado()
    {
        var cut = Renderizar(enPagina: true);

        cut.FindAll(".lista-relaciones-tarjeta").Should().ContainSingle();
        cut.FindAll(".workspace-lista-entidades").Should().BeEmpty("esa lista es la de filas-botón del panel y salía con viñetas en la página");

        var filas = cut.FindAll("li.fila-relacion");
        filas.Should().HaveCount(2);
        filas[0].QuerySelector(".fila-relacion-nombre")!.TextContent.Trim().Should().Be("Seguro de responsabilidad civil");
        filas[0].QuerySelector(".fila-relacion-cifra")!.TextContent.Trim().Should().Be("Caduca el 31/12/2026");
        filas[0].QuerySelector(".fila-relacion-estado [data-pieza=pastilla]")!.TextContent.Trim().Should().Be(EstadoDocumentoUi.Texto(EstadoDocumento.Vigente));
        filas[1].QuerySelector(".fila-relacion-cifra")!.TextContent.Trim().Should().Be("Falta la fecha de vencimiento");
        filas.Should().OnlyContain(f => f.ClassList.Contains("fila-relacion-compacta"));
        var gestionarVigente = filas[0].QuerySelector(".fila-relacion-acciones button")!;
        gestionarVigente.TextContent.Trim().Should().Be("Gestionar");
        gestionarVigente.ClassList.Should().Contain("gestionar-discreto");
        // Mismo patrón que Centro 360 › Trabajadores (#2142): botón secundario, no enlace de texto.
        filas.Select(f => f.QuerySelector(".fila-relacion-acciones button")!).Should()
            .OnlyContain(b => b.ClassList.Contains("boton-secundario") && b.ClassList.Contains("accion-gestionar-documento"));
        filas[1].QuerySelector(".fila-relacion-acciones button")!.TextContent.Trim().Should().Be("Gestionar");
    }

    /// <summary>En la página 360 el panel se abre sobre el Documento; dentro del panel el Documento se apila sobre la entidad.</summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task Abrir_un_documento_reemplaza_el_panel_en_pagina_y_lo_apila_en_el_panel(bool enPagina, int framesEsperados)
    {
        var cut = Renderizar(enPagina);
        var servicio = Services.GetRequiredService<ContextWorkspaceService>();
        await servicio.AbrirAsync(EntidadWorkspace.Empresa, Guid.NewGuid(), "Ibertec GmbH", "informacion");

        var objetivo = enPagina
            ? cut.Find("li.fila-relacion .fila-relacion-nombre")
            : cut.Find(".workspace-fila-entidad");
        await objetivo.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        servicio.Pila.Should().HaveCount(framesEsperados);
        servicio.FrameActual!.Tipo.Should().Be(EntidadWorkspace.Documento);
    }

    [Fact]
    public void Sin_EnPagina_el_panel_conserva_la_lista_de_filas_boton()
    {
        var cut = Renderizar(enPagina: false);

        cut.FindAll(".workspace-lista-entidades").Should().ContainSingle();
        cut.FindAll("li.fila-relacion").Should().BeEmpty();
    }
}
