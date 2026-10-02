using Bunit;
using CaeManager.Application.Blindaje42.Queries.ObtenerBlindajeEmpresasDeCliente;
using CaeManager.Web.Features.Blindaje42.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-33: «Blindaje 42.1» es nombre interno; la pestaña abre con una entradilla que dice qué es
/// (certificaciones de la TGSS del art. 42.1 ET), también sin datos.
/// </summary>
public class PestanaBlindaje42EntradillaTests : BunitContext
{
    private sealed class MediatorSinEmpresas : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(request switch
            {
                ObtenerBlindajeEmpresasDeClienteQuery => (object)Array.Empty<BlindajeEmpresaDto>(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            }));

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

    [Fact]
    public void La_pestana_abre_con_una_entradilla_que_explica_el_articulo_42_1()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<IMediator>(_ => new MediatorSinEmpresas());
        Services.AddSingleton(TimeProvider.System);
        Services.AddLocalization();
        Services.AddScoped<CaeManager.Web.Components.DesignSystem.ToastService>();

        var cut = Render<PestanaBlindaje42>(p => p.Add(c => c.EntidadId, Guid.NewGuid()));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Sin empresas"));
        cut.Find("[data-testid=blindaje42-entradilla]").TextContent
            .Should().Contain("art. 42.1 ET").And.Contain("TGSS");
    }
}
