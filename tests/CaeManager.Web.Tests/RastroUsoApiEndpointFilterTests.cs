using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Web.Api.V1;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>El rastro de <c>/api/v1</c> (C-11, D-5): fila antes de entregar datos, y sin fila no hay datos. Aquí con dobles; contra PostgreSQL, en <c>UsoApiDiarioBajoRlsTests</c>.</summary>
public class RastroUsoApiEndpointFilterTests
{
    private const string Ruta = "/api/v1/trabajadores/{id:guid}";

    private sealed class RastroFalso(bool falla = false) : IRastroUsoApi
    {
        public List<(Guid TenantId, Guid ApiKeyId, TipoClaveUsoApi Tipo, DateOnly Dia, string Endpoint)> Filas { get; } = [];

        public Task RegistrarPeticionAsync(
            Guid tenantId, Guid apiKeyId, TipoClaveUsoApi tipoClave, DateOnly dia, string endpoint, CancellationToken cancellationToken = default)
        {
            if (falla)
                throw new InvalidOperationException("Rastro no disponible.");
            Filas.Add((tenantId, apiKeyId, tipoClave, dia, endpoint));
            return Task.CompletedTask;
        }
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; } = tenantId;
    }

    private static async Task<bool> EjecutarAsync(
        RastroFalso rastro, Guid? tenantId, string? claveId, string esquema = ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema)
    {
        var claims = claveId is null ? new List<Claim>() : [new Claim(ClaimTypes.NameIdentifier, claveId)];
        var contexto = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IRastroUsoApi>(rastro)
                .AddSingleton<ITenantActual>(new TenantActualFalso(tenantId))
                .BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, esquema)),
        };
        contexto.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(Ruta), 0, EndpointMetadataCollection.Empty, "ruta"));

        var entregado = false;
        await new RastroUsoApiEndpointFilter().InvokeAsync(
            EndpointFilterInvocationContext.Create(contexto),
            _ =>
            {
                entregado = true;
                rastro.Filas.Should().ContainSingle("la fila se escribe antes de entregar los datos");
                return ValueTask.FromResult<object?>(Results.Ok());
            });
        return entregado;
    }

    [Theory]
    [InlineData(ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema, TipoClaveUsoApi.ClaveIntegracion)]
    [InlineData(ApiKeyAuthenticationSchemeOptions.NombreEsquema, TipoClaveUsoApi.ClaveSoporte)]
    public async Task Registra_tenant_clave_tipo_dia_de_negocio_y_patron_de_ruta(string esquema, TipoClaveUsoApi tipo)
    {
        var rastro = new RastroFalso();
        var (tenantId, claveId) = (Guid.NewGuid(), Guid.NewGuid());

        (await EjecutarAsync(rastro, tenantId, claveId.ToString(), esquema)).Should().BeTrue();

        rastro.Filas.Should().ContainSingle().Which.Should().Be((tenantId, claveId, tipo, DiaDeNegocio.Hoy(), Ruta));
    }

    [Fact]
    public async Task Si_el_rastro_falla_no_se_entregan_datos()
    {
        var acto = () => EjecutarAsync(new RastroFalso(falla: true), Guid.NewGuid(), Guid.NewGuid().ToString());

        await acto.Should().ThrowAsync<InvalidOperationException>().WithMessage("Rastro no disponible.");
    }

    [Theory]
    [InlineData(false, "c1b1a6f0-0000-4000-8000-000000000001")]
    [InlineData(true, null)]
    [InlineData(true, "no-es-un-guid")]
    public async Task Sin_tenant_o_sin_clave_no_se_entregan_datos(bool conTenant, string? claveId)
    {
        var rastro = new RastroFalso();

        var acto = () => EjecutarAsync(rastro, conTenant ? Guid.NewGuid() : null, claveId);

        await acto.Should().ThrowAsync<InvalidOperationException>();
        rastro.Filas.Should().BeEmpty();
    }
}
