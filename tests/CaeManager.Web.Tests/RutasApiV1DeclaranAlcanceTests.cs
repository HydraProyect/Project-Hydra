using System.Security.Claims;
using CaeManager.Domain.ApiKeys;
using CaeManager.Infrastructure.Autenticacion;
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
/// Los alcances son de denegación por defecto (ASVS V13.1.4): una ruta sin alcance declarado da 403 a la
/// Clave de Integración del Tenant, pero nada obligaría a declararlo si no fuera por este recorrido de las
/// rutas reales. Que <c>Program.cs</c> pase por <see cref="ApiV1.MapRecursosApiV1"/> y por el filtro lo
/// vigila <c>LimitadorApiPublicaRegistradoEnProgramTests</c> en Architecture.Tests.
/// </summary>
public class RutasApiV1DeclaranAlcanceTests
{
    [Fact]
    public async Task Cada_ruta_de_la_API_v1_declara_exactamente_un_alcance_conocido()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IMediator, Mediator>();
        await using var app = builder.Build();
        app.MapRecursosApiV1();

        var rutas = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();

        rutas.Should().HaveCount(9, "control positivo: dos por Cliente, Centro, Trabajador y Documento, y uno de Asignaciones");
        foreach (var ruta in rutas)
        {
            var declarados = ruta.Metadata.GetOrderedMetadata<AlcanceIntegracionRequerido>();
            declarados.Should().ContainSingle("la ruta {0} tiene que declarar su alcance", ruta.RoutePattern.RawText);
            AlcancesIntegracion.Todos.Should().Contain(declarados[0].Alcance);
        }
    }

    [Theory]
    [InlineData(ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema, AlcancesIntegracion.ClientesLeer, AlcancesIntegracion.ClientesLeer, true)]
    [InlineData(ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema, AlcancesIntegracion.ClientesLeer, AlcancesIntegracion.CentrosLeer, false)]
    [InlineData(ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema, null, AlcancesIntegracion.ClientesLeer, false)]
    [InlineData(ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema, AlcancesIntegracion.ClientesLeer, null, false)]
    [InlineData(ApiKeyAuthenticationSchemeOptions.NombreEsquema, AlcancesIntegracion.ClientesLeer, null, true)]
    [InlineData("OtroEsquema", AlcancesIntegracion.ClientesLeer, AlcancesIntegracion.ClientesLeer, false)]
    public async Task El_filtro_deniega_por_defecto_y_solo_la_Clave_de_Soporte_no_se_estrecha(
        string esquema, string? exigido, string? concedido, bool pasa)
    {
        var claims = concedido is null
            ? new List<Claim>()
            : [new(ClaveIntegracionAuthenticationSchemeOptions.TipoClaimAlcance, concedido)];
        var contexto = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, esquema)),
        };
        contexto.SetEndpoint(new Endpoint(null, exigido is null
            ? EndpointMetadataCollection.Empty
            : new EndpointMetadataCollection(new AlcanceIntegracionRequerido(exigido)), "ruta"));

        var ejecutado = false;
        var resultado = await new AlcanceIntegracionEndpointFilter().InvokeAsync(
            EndpointFilterInvocationContext.Create(contexto),
            _ =>
            {
                ejecutado = true;
                return ValueTask.FromResult<object?>(Results.Ok());
            });

        ejecutado.Should().Be(pasa);
        if (!pasa)
            ((IStatusCodeHttpResult)resultado!).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }
}
