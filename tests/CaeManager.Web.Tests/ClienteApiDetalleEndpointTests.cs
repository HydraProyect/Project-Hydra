using System.Text.Json;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Web.Api.V1;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

public class ClienteApiDetalleEndpointTests
{
    private static readonly Guid GestorCaeUsuarioId = Guid.NewGuid();
    private static readonly ClienteDetalleDto Detalle = new(Guid.NewGuid(), "Empresa de prueba", "B12345678", true,
        "Nota interna del equipo", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), GestorCaeUsuarioId, Guid.NewGuid(), true);

    [Fact]
    public async Task El_endpoint_de_detalle_no_publica_la_nota_interna()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IMediator>(new MediatorConDetalle());
        await using var app = builder.Build();
        app.MapClientesApiEndpoints();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/clientes/{id:guid}");

        using var scope = app.Services.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        contexto.Request.RouteValues["id"] = Detalle.Id.ToString();
        contexto.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(contexto);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        contexto.Response.Body.Position = 0;
        using var lector = new StreamReader(contexto.Response.Body);
        var cuerpo = await lector.ReadToEndAsync();
        cuerpo.Should().NotContain("Nota interna del equipo");
        using var json = JsonDocument.Parse(cuerpo);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "razonSocial", "cif", "esCritico", "creadoEnUtc", "ejecutivoUsuarioId", "version");
        json.RootElement.GetProperty("id").GetGuid().Should().Be(Detalle.Id);
        json.RootElement.GetProperty("ejecutivoUsuarioId").GetGuid().Should().Be(GestorCaeUsuarioId);
    }

    private sealed class MediatorConDetalle : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerClientePorIdQuery consulta && consulta.Id == Detalle.Id
                ? Task.FromResult((TResponse)(object)Detalle)
                : throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
