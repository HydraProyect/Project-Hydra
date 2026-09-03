using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerValidacionOficialDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// REC-097 (DEC-21): la pestaña "Versiones" se retiró del panel de Documento
/// porque anunciaba un versionado de archivos que no existe — mismo criterio
/// que #411 con "2fa" en el hub de Configuración (ver
/// <see cref="ConfiguracionTests"/> para el mismo patrón de test). Sin este
/// test, reintroducir la entrada no lo detectaría nadie hasta que un Gestor
/// CAE clicara la pestaña.
/// </summary>
public class DocumentoWorkspacePanelTests : BunitContext
{
    /// <summary>El panel lanza dos consultas distintas por el mismo IMediator — responde por tipo.</summary>
    private sealed class MediatorDocumento : IMediator
    {
        public required DocumentoDetalleDto Detalle { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(request switch
            {
                ObtenerDocumentoPorIdQuery => (object?)Detalle,
                ObtenerValidacionOficialDocumentoQuery => null,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            })!);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private static DocumentoDetalleDto Detalle() => new(
        Id: Guid.NewGuid(),
        Ambito: AmbitoAplicacion.Empresa,
        PropietarioNombre: "Refrielectric S.A.",
        TipoDocumentoNombre: "Certificado TGSS",
        TipoDocumentoAplicaVencimientoAutomatico: false,
        FechaEmision: new DateOnly(2026, 1, 1),
        FechaVencimiento: null,
        ArchivoUrl: null,
        Comentarios: null,
        TipoDocumentoDescripcion: null,
        TipoDocumentoCriteriosValidacion: null,
        TipoDocumentoSeSolicitaA: null,
        TipoDocumentoObservaciones: null,
        Version: Guid.NewGuid(),
        TipoDocumentoPerfilDocumentoOficial: PerfilDocumentoOficial.Ninguno,
        EmpresaId: Guid.NewGuid());

    private IRenderedComponent<DocumentoWorkspacePanel> Renderizar(DocumentoDetalleDto detalle, string pestanaActiva)
    {
        Services.AddScoped<IMediator>(_ => new MediatorDocumento { Detalle = detalle });
        return Render<DocumentoWorkspacePanel>(parametros => parametros
            .Add(p => p.EntidadId, detalle.Id)
            .Add(p => p.PestanaActiva, pestanaActiva));
    }

    [Fact]
    public void El_conjunto_de_pestanas_no_ofrece_Versiones()
    {
        var cut = Renderizar(Detalle(), "informacion");

        var pestanas = cut.FindAll("[role=tab]").Select(boton => boton.TextContent.Trim()).ToList();

        pestanas.Should().Contain("Validación").And.Contain("Historial");
        pestanas.Should().NotContain("Versiones",
            "REC-097/DEC-21 la retiró: anunciar un versionado que no existe es peor que no tenerlo");
    }

    [Fact]
    public void Un_deep_link_guardado_a_versiones_cae_a_Informacion_sin_dejar_el_panel_vacio()
    {
        var detalle = Detalle();
        var cut = Renderizar(detalle, "versiones");

        cut.Find("[role=tab][aria-selected='true']").TextContent.Trim().Should().Be("Información");
        cut.Find(".pestanas-panel").TextContent.Should().Contain(detalle.PropietarioNombre,
            "un ctx=…:versiones guardado antes de REC-097 no puede dejar el panel sin contenido");
    }
}
