using Bunit;
using CaeManager.Application.Visitas.Queries.ObtenerVisitasAnterioresDeCentro;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class CentroVisitasAnterioresTests : BunitContext
{
    public CentroVisitasAnterioresTests() => Services.AddLocalization();

    [Fact]
    public void Muestra_fechas_como_texto_nombres_y_enlace_a_visita_360()
    {
        var visita = Guid.NewGuid();
        Services.AddSingleton<IMediator>(new MediadorFalso([
            new(visita, new(2026, 10, 1), new(2026, 10, 2), false, true, ["Ana Pérez"])]));

        var componente = Render<CentroVisitasAnteriores>(p => p.Add(x => x.CentroId, Guid.NewGuid()));

        componente.Markup.Should().Contain("01/10/2026").And.Contain("02/10/2026").And.Contain("Ana Pérez");
        componente.Find($"a[href='/visitas/{visita}']").Should().NotBeNull();
        componente.Markup.Should().NotContain("Gestionada por").And.NotContain("fueron");
    }

    [Fact]
    public void Sin_datos_muestra_vacio()
    {
        Services.AddSingleton<IMediator>(new MediadorFalso([]));
        var componente = Render<CentroVisitasAnteriores>(p => p.Add(x => x.CentroId, Guid.NewGuid()));
        componente.Markup.Should().Contain("Sin visitas anteriores");
    }

    private sealed class MediadorFalso(IReadOnlyList<VisitaAnteriorDeCentroDto> visitas) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)visitas);
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}
