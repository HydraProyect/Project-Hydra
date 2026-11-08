using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
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
        RastroFalso rastro, Guid? tenantId, string? claveId, string esquema = ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema,
        Guid? tenantDeLaClave = null, bool claveSinTenant = false)
    {
        var claims = claveId is null ? new List<Claim>() : [new Claim(ClaimTypes.NameIdentifier, claveId)];
        if (!claveSinTenant && (tenantDeLaClave ?? tenantId) is { } tenantDelClaim)
            claims.Add(new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, tenantDelClaim.ToString()));
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
        using var reloj = RelojDePrueba.FijarEn(DateTimeOffset.UtcNow);
        var rastro = new RastroFalso();
        var (tenantId, claveId) = (Guid.NewGuid(), Guid.NewGuid());

        (await EjecutarAsync(rastro, tenantId, claveId.ToString(), esquema)).Should().BeTrue();

        rastro.Filas.Should().ContainSingle().Which.Should().Be((tenantId, claveId, tipo, DiaDeNegocio.Hoy(), Ruta));
    }

    // MapOpenApi devuelve un constructor genérico: esto comprueba que el filtro llega de verdad a su ruta. Sin
    // AddOpenApi, que aquí activaría el generador del paquete, no hay documento que servir: basta con el filtro.
    [Fact]
    public async Task El_documento_OpenAPI_servido_a_una_clave_deja_rastro()
    {
        using var reloj = RelojDePrueba.FijarEn(DateTimeOffset.UtcNow);
        var rastro = new RastroFalso();
        var (tenantId, claveId) = (Guid.NewGuid(), Guid.NewGuid());
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IRastroUsoApi>(rastro);
        builder.Services.AddSingleton<ITenantActual>(new TenantActualFalso(tenantId));
        await using var app = builder.Build();
        app.MapOpenApi("/api/v1/openapi.json").AddEndpointFilter<IEndpointConventionBuilder, RastroUsoApiEndpointFilter>();
        var ruta = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().Single();
        var http = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, claveId.ToString()),
                    new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, tenantId.ToString()),
                ],
                ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema)),
        };
        http.Response.Body = Stream.Null;
        http.SetEndpoint(ruta);

        await ruta.RequestDelegate!(http);

        rastro.Filas.Should().ContainSingle().Which.Should().Be(
            (tenantId, claveId, TipoClaveUsoApi.ClaveIntegracion, DiaDeNegocio.Hoy(), "/api/v1/openapi.json"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Si_el_Tenant_resuelto_no_es_el_de_la_clave_no_se_entregan_datos(bool claveSinTenant)
    {
        var rastro = new RastroFalso();

        var acto = () => EjecutarAsync(
            rastro, Guid.NewGuid(), Guid.NewGuid().ToString(), tenantDeLaClave: Guid.NewGuid(), claveSinTenant: claveSinTenant);

        await acto.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no es el de la clave*");
        rastro.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_esquema_que_no_es_de_clave_no_se_atribuye_a_ninguna_de_las_dos()
    {
        var rastro = new RastroFalso();

        var acto = () => EjecutarAsync(rastro, Guid.NewGuid(), Guid.NewGuid().ToString(), esquema: "Cookies");

        await acto.Should().ThrowAsync<InvalidOperationException>().WithMessage("*esquema*");
        rastro.Filas.Should().BeEmpty();
    }
}
