using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Extension;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El endpoint de la extensión es el único consumidor que pide la lista sin rechazos vigentes: si dejara de
/// pasar <c>SoloSubiblesExtension</c>, la extensión volvería a ofrecer una acreditación Rechazada que el
/// servidor después se niega a marcar como subida. La política de autorización se sustituye por una que
/// siempre pasa: aquí solo se comprueba qué consulta se envía, no quién puede pedirla.
/// </summary>
public class AcreditacionesPendientesEndpointExtensionTests
{
    [Fact]
    public async Task El_endpoint_de_la_extension_pide_la_lista_sin_rechazos_vigentes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var mediador = new MediadorQueRegistra();
        builder.Services.AddSingleton<IMediator>(mediador);
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization(o => o.AddPolicy(Policies.SesionOExtension, p => p.RequireAssertion(_ => true)));
        await using var app = builder.Build();
        app.UseAuthorization();
        app.MapAcreditacionesPendientesEndpoints();
        await app.StartAsync();
        try
        {
            var direccion = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var cliente = new HttpClient { BaseAddress = new Uri(direccion) };
            var respuesta = await cliente.GetAsync("/extension/acreditaciones-pendientes");

            ((int)respuesta.StatusCode).Should().Be(200);
            mediador.Consulta.Should().NotBeNull();
            mediador.Consulta!.SoloSubiblesExtension.Should().BeTrue();
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed class MediadorQueRegistra : IMediator
    {
        public ObtenerAcreditacionesPorProveedorQuery? Consulta { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Consulta = request as ObtenerAcreditacionesPorProveedorQuery;
            return Task.FromResult((TResponse)(object)(IReadOnlyList<ProveedorAcreditacionesDto>)[]);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
