using System.Text.Json;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
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
/// <c>GET /api/v1/centros/{id}</c> ejecutado de verdad: el DTO interno de detalle lleva la ficha de acceso y
/// prevención de Centro 360, con contactos personales, y una clave de la API pública entra con rol Consulta, que sí
/// puede leer esa ficha en la aplicación. La API publica <see cref="CentroApiDetalleDto"/>, el contrato previo; volver
/// a devolver el DTO interno pone este test en rojo.
/// </summary>
public class CentroApiDetalleEndpointTests
{
    private static readonly CentroDetalleDto Detalle = new(Guid.NewGuid(), Guid.NewGuid(), "Titular S.A.", Guid.NewGuid(),
        "Ejecutora S.L.", "Centro de prueba", "C-01", "Calle de prueba 1", "Recepción", null, Guid.NewGuid(),
        HorarioAcceso: "7 a 19", ControlAccesosNombre: "Persona accesos", ControlAccesosTelefono: "600111222",
        TecnicoPrevencionNombre: "Persona prevencion", TecnicoPrevencionTelefono: "600333444",
        TecnicoPrevencionCorreo: "prl@example.test", TrabajosEnAltura: true, EspaciosConfinados: false,
        AtmosferasExplosivas: true, FichaVisible: true,
        // Una clave de API entra con rol Consulta, que en la aplicación sí lee la nota interna: la API no la publica.
        Notas: "Nota interna del equipo", NotaInternaVisible: true);

    [Fact]
    public async Task El_endpoint_de_detalle_no_publica_la_ficha_de_acceso_y_prevencion()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IMediator>(new MediatorConDetalle());
        await using var app = builder.Build();
        app.MapCentrosApiEndpoints();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/centros/{id:guid}");

        using var scope = app.Services.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        contexto.Request.RouteValues["id"] = Detalle.Id.ToString();
        contexto.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(contexto);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        contexto.Response.Body.Position = 0;
        using var lector = new StreamReader(contexto.Response.Body);
        var cuerpo = await lector.ReadToEndAsync();
        cuerpo.Should().NotContain("Persona accesos").And.NotContain("600111222").And.NotContain("Persona prevencion")
            .And.NotContain("600333444").And.NotContain("prl@example.test").And.NotContain("7 a 19")
            .And.NotContain("Nota interna del equipo").And.NotContainEquivalentOf("notas");
        using var json = JsonDocument.Parse(cuerpo);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "clienteId", "clienteRazonSocial", "empresaId", "empresaRazonSocial", "nombre", "codigoCentro",
            "direccion", "contacto", "contratoVigenteHasta", "version", "gestionCae");
        json.RootElement.GetProperty("id").GetGuid().Should().Be(Detalle.Id);
    }

    private sealed class MediatorConDetalle : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerCentroPorIdQuery consulta && consulta.Id == Detalle.Id
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
