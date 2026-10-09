using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las páginas y endpoints que el Encargo de administración no abre (decisión D-8, 2026-10-08) se
/// niegan a quien lleva el claim del encargo aunque su rol elevado cumpla el
/// <c>[Authorize(Roles = Administrador)]</c>; a cualquier otro principal, el handler no le hace nada.
/// </summary>
public class PaginasExcluidasDelEncargoAuthorizationHandlerTests
{
    private static readonly AuthorizationPolicy SoloAdministrador =
        new AuthorizationPolicyBuilder().RequireRole(Roles.Administrador).Build();

    public static TheoryData<Type> PaginasExcluidas =>
    [
        typeof(Features.ApiKeys.Pages.ClavesApi),
        typeof(Features.Auditoria.Pages.Auditoria),
        typeof(Features.Auditoria.Pages.AccesosDocumentosSensibles),
        typeof(Features.AuditoriaIa.Pages.AuditoriaIa),
        typeof(Features.Clientes.Pages.ImportarClientes),
        typeof(Features.Clientes.Pages.ImportarCombinado),
        typeof(Features.Configuracion.Pages.SeleccionarClienteLecturaIa),
        typeof(Features.Documentos.Pages.ImportarDocumentos),
        typeof(Features.Facturacion.Pages.Facturacion),
        typeof(Features.GestionRoles.Pages.Roles),
        typeof(Features.Importacion.Pages.Importacion),
        typeof(Features.Integraciones.Pages.Conexiones),
        typeof(Features.Retencion.Pages.Retencion),
    ];

    public static TheoryData<Type> PaginasPermitidas =>
    [
        typeof(Features.Configuracion.Pages.Configuracion),
        typeof(Features.TiposDocumento.Pages.TiposDocumento),
        typeof(Features.Usuarios.Pages.Usuarios),
        typeof(Features.Clientes.Pages.ConfiguracionIaCliente),
    ];

    [Theory]
    [MemberData(nameof(PaginasExcluidas))]
    public async Task A_quien_administra_por_encargo_se_le_niega_la_pagina_excluida_aunque_cumpla_el_rol(Type pagina)
    {
        (await AutorizarAsync(Administrador(porEncargo: true), Ruta(pagina))).Succeeded.Should().BeFalse(
            "un Fail() explícito gana al requisito de rol que el rol elevado satisface");
    }

    [Theory]
    [MemberData(nameof(PaginasExcluidas))]
    public async Task Un_Administrador_propio_sigue_entrando_en_esas_mismas_paginas(Type pagina)
    {
        (await AutorizarAsync(Administrador(porEncargo: false), Ruta(pagina))).Succeeded.Should().BeTrue(
            "el handler solo restringe a quien lleva el claim del encargo");
    }

    [Theory]
    [MemberData(nameof(PaginasPermitidas))]
    public async Task A_quien_administra_por_encargo_no_se_le_niegan_las_paginas_que_el_encargo_abre(Type pagina)
    {
        (await AutorizarAsync(Administrador(porEncargo: true), Ruta(pagina))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task El_primer_render_por_HTTP_de_una_pagina_excluida_tambien_se_niega()
    {
        // El middleware de autorización evalúa el endpoint de la página con el HttpContext como recurso.
        var http = PeticionA(new ComponentTypeMetadata(typeof(Features.Retencion.Pages.Retencion)));

        (await AutorizarAsync(Administrador(porEncargo: true), http)).Succeeded.Should().BeFalse();
        (await AutorizarAsync(Administrador(porEncargo: false), http)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Un_endpoint_minimo_marcado_como_excluido_se_niega_y_uno_sin_marca_no()
    {
        var marcado = PeticionA(EndpointExcluidoDelEncargo.Instancia);
        var sinMarca = PeticionA();

        (await AutorizarAsync(Administrador(porEncargo: true), marcado)).Succeeded.Should().BeFalse();
        (await AutorizarAsync(Administrador(porEncargo: false), marcado)).Succeeded.Should().BeTrue();
        (await AutorizarAsync(Administrador(porEncargo: true), sinMarca)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_recurso_no_niega_nada()
    {
        // Así consulta el hub de Configuración sus políticas: la barrera de sus paneles es el propio hub.
        (await AutorizarAsync(Administrador(porEncargo: true), recurso: null)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void La_extension_de_endpoint_deja_la_marca_en_los_metadatos()
    {
        var constructor = WebApplication.CreateBuilder();
        var app = constructor.Build();

        app.MapGet("/x", () => "x").ExcluidoDelEncargoDeAdministracion();

        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).Should().ContainSingle()
            .Which.Metadata.GetMetadata<EndpointExcluidoDelEncargo>().Should().NotBeNull();
    }

    private static Task<AuthorizationResult> AutorizarAsync(ClaimsPrincipal usuario, object? recurso)
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddAuthorizationCore();
        servicios.AddSingleton<IAuthorizationHandler, PaginasExcluidasDelEncargoAuthorizationHandler>();

        return servicios.BuildServiceProvider().GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(usuario, recurso, SoloAdministrador);
    }

    private static ClaimsPrincipal Administrador(bool porEncargo)
    {
        var identidad = new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.Administrador)], "prueba");
        if (porEncargo)
            identidad.AddClaim(new Claim(RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion, Guid.NewGuid().ToString()));
        return new ClaimsPrincipal(identidad);
    }

    private static Microsoft.AspNetCore.Components.RouteData Ruta(Type pagina) =>
        new(pagina, new Dictionary<string, object?>());

    private static DefaultHttpContext PeticionA(params object[] metadatos)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadatos), "prueba"));
        return http;
    }
}
