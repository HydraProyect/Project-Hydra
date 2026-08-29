using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Reportes;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los cuatro informes descargables se servían solo detrás del FallbackPolicy
/// global (cualquier usuario autenticado), a diferencia de
/// FacturacionEndpoints, que sí exigía rol. Un usuario con rol Cliente —que ni
/// siquiera ve la pantalla /reportes— podía pedir el archivo directamente por
/// URL. Este test fija la regla: los endpoints exigen exactamente los mismos
/// roles que la página (Reportes.razor), ni uno más.
///
/// La comprobación de cartera (que el clienteId pedido sea del usuario) es el
/// otro lado del mismo agujero y vive en AlcanceDeCarteraEnReportesTests, en
/// la capa Application.
/// </summary>
public class ReportesEndpointsAutorizacionTests
{
    private static readonly string[] RolesEsperados =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta];

    [Fact]
    public void Los_cuatro_informes_exigen_los_mismos_roles_que_la_pagina_de_reportes()
    {
        var endpoints = MapearEndpoints();

        endpoints.Select(e => ((RouteEndpoint)e).RoutePattern.RawText).Should().BeEquivalentTo([
            "/reportes/vigencia.xlsx", "/reportes/vigencia.pdf",
            "/reportes/asignaciones.xlsx", "/reportes/asignaciones.pdf"
        ]);

        foreach (var endpoint in endpoints)
        {
            var politica = endpoint.Metadata.GetMetadata<AuthorizationPolicy>();
            politica.Should().NotBeNull($"{endpoint.DisplayName} tiene que exigir autorización por rol, no solo estar autenticado");

            politica!.Requirements.OfType<RolesAuthorizationRequirement>()
                .SelectMany(r => r.AllowedRoles)
                .Should().BeEquivalentTo(RolesEsperados);
        }
    }

    [Fact]
    public void Ningun_informe_es_accesible_para_el_rol_Cliente()
    {
        var rolesPermitidos = MapearEndpoints()
            .SelectMany(e => e.Metadata.GetMetadata<AuthorizationPolicy>()?.Requirements.OfType<RolesAuthorizationRequirement>() ?? [])
            .SelectMany(r => r.AllowedRoles);

        rolesPermitidos.Should().NotContain(Roles.Cliente,
            "el rol Cliente no tiene acceso a /reportes, así que tampoco a la descarga de sus informes");
    }

    private static IReadOnlyList<Endpoint> MapearEndpoints()
    {
        var servicios = new ServiceCollection();
        servicios.AddRouting();
        // Basta con que IMediator ESTÉ registrado: el mapeo solo consulta
        // IServiceProviderIsService para no inferir el parámetro como cuerpo
        // de la petición; nunca llega a resolverlo porque no se ejecuta
        // ninguna petición real.
        servicios.AddSingleton<IMediator>(_ => throw new NotSupportedException("Este test solo lee metadatos, no ejecuta los endpoints."));
        var rutas = new ConstructorRutasEnMemoria(servicios.BuildServiceProvider());

        rutas.MapReportesEndpoints();

        return rutas.DataSources.SelectMany(d => d.Endpoints).ToList();
    }

    /// <summary>Lo mínimo para poder mapear los endpoints y leer sus metadatos sin levantar un host web entero.</summary>
    private sealed class ConstructorRutasEnMemoria(IServiceProvider servicios) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = servicios;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
}
