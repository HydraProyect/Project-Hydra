using System.Security.Claims;
using System.Text.Json;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Web.Api.V1;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>GET /api/v1/asignaciones</c> con Clave de Integración del Tenant devuelve
/// <see cref="AsignacionApiListaDto"/>. El contrato JSON conserva <c>clienteId</c> y <c>clienteNombre</c>:
/// es lo publicado, aunque en C# el campo se llame por su concepto canónico.
/// </summary>
public class AsignacionApiDtosTests
{
    private sealed class ConsultaFalsa : IRequestHandler<ObtenerAsignacionesQuery, ResultadoPaginado<AsignacionListaDto>>
    {
        public Task<ResultadoPaginado<AsignacionListaDto>> Handle(ObtenerAsignacionesQuery request, CancellationToken cancellationToken) =>
            Task.FromResult(new ResultadoPaginado<AsignacionListaDto>([], 0, 1, 20));
    }

    // Hoy los dos tipos serializan igual, así que el JSON no distingue cuál salió: se mira el tipo que devuelve la ruta real.
    [Theory]
    [InlineData(ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema, typeof(Ok<ResultadoPaginado<AsignacionApiListaDto>>))]
    [InlineData(ApiKeyAuthenticationSchemeOptions.NombreEsquema, typeof(Ok<ResultadoPaginado<AsignacionListaDto>>))]
    public async Task La_ruta_entrega_la_proyeccion_publica_y_la_Clave_de_Soporte_conserva_la_suya(string esquema, Type esperado)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IRequestHandler<ObtenerAsignacionesQuery, ResultadoPaginado<AsignacionListaDto>>, ConsultaFalsa>();
        builder.Services.AddSingleton<IMediator, Mediator>();
        await using var app = builder.Build();
        object? devuelto = null;
        app.MapGroup("/api/v1")
            .AddEndpointFilter(async (contexto, siguiente) => devuelto = await siguiente(contexto))
            .MapRecursosApiV1();
        var ruta = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(r => r.RoutePattern.RawText == "/api/v1/asignaciones");
        var http = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity([], esquema)),
        };
        http.Response.Body = Stream.Null;

        await ruta.RequestDelegate!(http);

        devuelto.Should().BeOfType(esperado);
    }

    [Fact]
    public void La_proyeccion_conserva_los_campos_del_contrato_y_su_orden()
    {
        var (id, trabajadorId, centroId, clienteEmpresarialId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var dto = new AsignacionListaDto(
            id, trabajadorId, "Nora Vidal", centroId, "Planta Alfa", clienteEmpresarialId, "Alfa Montajes S.L.",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var opciones = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(AsignacionApiListaDto.DesdeInterno(dto), opciones));

        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "trabajadorId", "trabajadorNombre", "centroId", "centroNombre", "clienteId", "clienteNombre", "fechaAlta", "fechaBaja");
        json.RootElement.GetProperty("id").GetGuid().Should().Be(id);
        json.RootElement.GetProperty("trabajadorId").GetGuid().Should().Be(trabajadorId);
        json.RootElement.GetProperty("centroId").GetGuid().Should().Be(centroId);
        json.RootElement.GetProperty("clienteId").GetGuid().Should().Be(clienteEmpresarialId);
        json.RootElement.GetProperty("trabajadorNombre").GetString().Should().Be("Nora Vidal");
        json.RootElement.GetProperty("centroNombre").GetString().Should().Be("Planta Alfa");
        json.RootElement.GetProperty("fechaAlta").GetString().Should().Be("2026-01-01");
        json.RootElement.GetProperty("clienteNombre").GetString().Should().Be("Alfa Montajes S.L.");
        json.RootElement.GetProperty("fechaBaja").GetString().Should().Be("2026-12-31");
    }
}
