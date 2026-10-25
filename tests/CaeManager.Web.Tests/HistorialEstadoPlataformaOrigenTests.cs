using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCambiosEstadoPlataformaDeCentro;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Incremento E1 de la extensión: un cambio anotado con «Lo he enviado» de la extensión se distingue en el Historial de
/// Centro 360 de uno anotado desde Centro 360, y uno deshecho sigue marcado.
/// </summary>
public class HistorialEstadoPlataformaOrigenTests : BunitContext
{
    private static CambioEstadoPlataformaCentroDto Cambio(OrigenCambioEstadoAcreditacion origen, bool deshecho = false) => new(
        Guid.NewGuid(), new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc), "Reconocimiento médico", "Ana Ruiz", "Dokify",
        EstadoAcreditacion.PendienteDeSubir, EstadoAcreditacion.Subida, new DateOnly(2026, 10, 25), null, null, "Marta",
        deshecho, origen);

    private IRenderedComponent<HistorialEstadoPlataformaCentro> Pintar(params CambioEstadoPlataformaCentroDto[] cambios)
    {
        Services.AddLocalization();
        Services.AddSingleton<IMediator>(new Mediador(cambios));
        return Render<HistorialEstadoPlataformaCentro>(p => p.Add(c => c.CentroId, Guid.NewGuid()));
    }

    [Fact]
    public void Un_cambio_de_la_extension_lleva_desde_la_extension()
    {
        var corte = Pintar(Cambio(OrigenCambioEstadoAcreditacion.Extension));

        var fila = corte.Find("[data-cambio-plataforma]");
        fila.QuerySelector("[data-origen-extension]").Should().NotBeNull();
        fila.TextContent.Should().Contain("Desde la extensión");
    }

    [Fact]
    public void Un_cambio_de_Centro_360_no_lo_lleva()
    {
        var corte = Pintar(Cambio(OrigenCambioEstadoAcreditacion.Centro360));

        corte.FindAll("[data-cambio-plataforma]").Should().ContainSingle();
        corte.FindAll("[data-origen-extension]").Should().BeEmpty();
    }

    [Fact]
    public void Un_cambio_de_la_extension_deshecho_sigue_en_el_historial_marcado()
    {
        var corte = Pintar(Cambio(OrigenCambioEstadoAcreditacion.Extension, deshecho: true));

        var fila = corte.Find("[data-cambio-plataforma]");
        fila.ClassList.Should().Contain("historial-estado-plataforma-deshecho");
        fila.TextContent.Should().Contain("Desde la extensión").And.Contain("Deshecho");
    }

    private sealed class Mediador(IReadOnlyList<CambioEstadoPlataformaCentroDto> cambios) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerCambiosEstadoPlataformaDeCentroQuery
                ? Task.FromResult((TResponse)cambios)
                : throw new NotSupportedException(request.GetType().Name);
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }
}
