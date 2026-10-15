using System.Text.Json;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Web.Api.V1;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>GET /api/v1/trabajadores/{id}</c> ejecutado de verdad, no solo la proyección: el DTO interno de detalle lleva
/// la ficha de altas en plataforma (número de afiliación a la Seguridad Social, nacionalidad, permiso de trabajo) y
/// la API pública no la publica. <see cref="TrabajadorApiDtosTests"/> fija la forma de
/// <see cref="TrabajadorApiDetalleDto"/>; este caso fija que el endpoint la use, de modo que volver a devolver el DTO
/// interno ponga la suite en rojo.
/// </summary>
public class TrabajadorApiDetalleEndpointTests
{
    private static readonly TrabajadorDetalleDto Detalle = new(Guid.NewGuid(), Guid.NewGuid(), null, "Montajes Ebro",
        "Nora", "Vidal", "12345678Z", new(1988, 3, 14), "nora@example.test", null, null, null, "Electricista",
        Guid.NewGuid(), NumeroAfiliacionSeguridadSocial: "281234567840", Nacionalidad: "Colombiana",
        TipoPermisoTrabajo: "Residencia y trabajo", MotivoPermisoTrabajo: "Se identifica con NIE");

    [Fact]
    public async Task El_endpoint_de_detalle_no_publica_la_ficha_de_altas_en_plataforma()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IMediator>(new MediatorConDetalle());
        await using var app = builder.Build();
        app.MapTrabajadoresApiEndpoints();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/trabajadores/{id:guid}");

        using var scope = app.Services.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        contexto.Request.RouteValues["id"] = Detalle.Id.ToString();
        contexto.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(contexto);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        contexto.Response.Body.Position = 0;
        using var lector = new StreamReader(contexto.Response.Body);
        var cuerpo = await lector.ReadToEndAsync();
        cuerpo.Should().NotContain("281234567840").And.NotContain("Colombiana").And.NotContain("Se identifica con NIE");
        using var json = JsonDocument.Parse(cuerpo);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "empresaId", "subcontrataId", "empleadorNombre", "nombre", "apellidos", "dni",
            "fechaNacimiento", "email", "observaciones", "alias", "telefono", "puesto", "version");
        json.RootElement.GetProperty("id").GetGuid().Should().Be(Detalle.Id);
    }

    private sealed class MediatorConDetalle : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerTrabajadorPorIdQuery consulta && consulta.Id == Detalle.Id
                ? Task.FromResult((TResponse)(object)Detalle)
                : throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");

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
