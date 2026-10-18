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

    private IRenderedComponent<PestanaDocumentacion> Renderizar(bool enPagina, params DocumentoListaDto[] adicionales)
    {
        var documentos = new List<DocumentoListaDto>
        {
            new(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH", "Seguro de responsabilidad civil",
                new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31), EstadoDocumento.Vigente, null, []),
            new(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH", "Certificado de la Seguridad Social",
                new DateOnly(2025, 1, 1), null, EstadoDocumento.Faltante, null, []),
        };
        documentos.AddRange(adicionales);
        Services.AddScoped<IMediator>(_ => new MediatorFalso(documentos));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddLocalization();

        var cut = Render<PestanaDocumentacion>(p => p
            .Add(x => x.Ambito, AmbitoAplicacion.Empresa)
            .Add(x => x.PropietarioId, Guid.NewGuid())
            .Add(x => x.EnPagina, enPagina));
        cut.WaitForAssertion(() => cut.FindAll("li").Should().HaveCount(documentos.Count));
        return cut;
    }

    [Fact]
    public void En_pagina_los_documentos_son_filas_de_una_tarjeta_con_vigencia_y_pastilla_de_estado()
    {
        var cut = Renderizar(enPagina: true);

        // Maqueta «Documentación agrupada»: UNA tarjeta; cada grupo es una barra de cabecera dentro de ella, no un h3 suelto con una tarjeta por grupo.
        cut.FindAll(".documentacion-tarjeta").Should().ContainSingle();
        cut.FindAll(".lista-relaciones-tarjeta").Should().BeEmpty("cada grupo ya no es una tarjeta aparte");
        var cabeceras = cut.FindAll(".documentacion-tarjeta > h3.documentacion-grupo");
        cabeceras.Select(h => h.QuerySelector(".documentacion-grupo-titulo")!.TextContent.Trim()).Should().Equal("Seguros y mutua", "Sin grupo");
        cabeceras.Select(h => h.QuerySelector(".documentacion-grupo-recuento")!.TextContent.Trim()).Should().Equal("1", "1");
        cut.FindAll(".workspace-lista-entidades").Should().BeEmpty("esa lista es la de filas-botón del panel y salía con viñetas en la página");

        var filas = cut.FindAll("li.fila-relacion");
        filas.Should().HaveCount(2);
        filas[0].QuerySelector(".fila-relacion-nombre")!.TextContent.Trim().Should().Be("Seguro de responsabilidad civil");
        filas[0].QuerySelector("[data-pieza=documento-sublinea]")!.TextContent.Trim().Should().Be("vence 31/12/2026");
        filas[0].QuerySelector(".fila-relacion-estado [data-pieza=pastilla]")!.TextContent.Trim().Should().Be(EstadoDocumentoUi.Texto(EstadoDocumento.Vigente));
        filas[1].QuerySelector("[data-pieza=documento-sublinea]")!.TextContent.Trim().Should().Be("vence —");
    }

    [Fact]
    public void En_pagina_la_sublinea_de_un_tipo_archivado_lo_marca_antes_del_vencimiento()
    {
        var archivado = new DocumentoListaDto(Guid.NewGuid(), AmbitoAplicacion.Empresa, "Ibertec GmbH", "Protocolo frente al acoso",
            new DateOnly(2025, 1, 1), new DateOnly(2027, 3, 4), EstadoDocumento.Vigente, null, []);

        var cut = Renderizar(enPagina: true, archivado);

        var fila = cut.FindAll("li.fila-relacion").Single(f => f.QuerySelector(".fila-relacion-nombre")!.TextContent.Trim() == "Protocolo frente al acoso");
        fila.QuerySelector("[data-pieza=documento-sublinea]")!.TextContent.Trim().Should().Be("Tipo archivado · vence 04/03/2027");
        cut.FindAll(".documentacion-tarjeta").Should().ContainSingle("tres grupos, una sola tarjeta");
        cut.FindAll(".documentacion-tarjeta > h3.documentacion-grupo").Should().HaveCount(3);
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
