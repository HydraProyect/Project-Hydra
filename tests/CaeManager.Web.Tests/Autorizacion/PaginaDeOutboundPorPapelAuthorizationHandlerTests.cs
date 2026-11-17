using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services.Autorizacion;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests.Autorizacion;

/// <summary>
/// Las páginas de Outbound se niegan con papel Inbound aunque el rol cumpla su <c>[Authorize]</c> (cierre del hueco
/// declarado de B-02b): mismo criterio que la guarda de comandos (<see cref="CriterioDePapel"/>). El Administrador del
/// Tenant y el Gestor CAE interno, con papel Tenant de origen, no pierden nada; el papel Outbound tampoco.
/// </summary>
public class PaginaDeOutboundPorPapelAuthorizationHandlerTests
{
    private static readonly Type MiTrabajo = typeof(Features.Bandeja.Pages.MiTrabajo);

    /// <summary>El <c>[Authorize(Roles = …)]</c> de Mi trabajo, para que el rol cumpla y solo decida el papel.</summary>
    private static readonly AuthorizationPolicy RolesDeMiTrabajo = new AuthorizationPolicyBuilder()
        .RequireRole(Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae)
        .Build();

    [Theory]
    [InlineData(Roles.GestorCae, PapelActivo.Outbound, true)]
    [InlineData(Roles.GestorCae, PapelActivo.Inbound, false)]
    [InlineData(Roles.GestorCae, PapelActivo.TenantDeOrigen, true)]
    [InlineData(Roles.Administrador, PapelActivo.TenantDeOrigen, true)]
    [InlineData(Roles.Administrador, PapelActivo.Inbound, false)]
    [InlineData(Roles.GestorCae, PapelActivo.OperacionQueYaNoAutoriza, false)]
    [InlineData(Roles.GestorCae, PapelActivo.SinPapel, true)]
    public async Task Una_pagina_de_Outbound_solo_se_abre_con_un_papel_que_admite_lo_que_no_es_Inbound(
        string rol, PapelActivo papel, bool entra)
    {
        var usuario = new UsuarioConPapel(papel);

        var resultado = await AutorizarAsync(Con(rol), Ruta(MiTrabajo), usuario);

        resultado.Succeeded.Should().Be(entra, "el rol cumple el [Authorize] de la página; decide el papel {0}", papel);
        usuario.ConsultasDePapel.Should().Be(1);
    }

    [Fact]
    public async Task El_primer_render_por_HTTP_de_una_pagina_de_Outbound_tambien_se_niega_con_papel_Inbound()
    {
        // El middleware de autorización evalúa el endpoint de la página con el HttpContext como recurso.
        var http = PeticionA(new ComponentTypeMetadata(MiTrabajo));

        (await AutorizarAsync(Con(Roles.GestorCae), http, new UsuarioConPapel(PapelActivo.Inbound))).Succeeded.Should().BeFalse();
        (await AutorizarAsync(Con(Roles.GestorCae), http, new UsuarioConPapel(PapelActivo.Outbound))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Un_endpoint_minimo_de_Outbound_se_niega_con_papel_Inbound_y_uno_sin_marca_no()
    {
        // Quien exporta un listado es quien lo ve: la exportación de una página de Outbound lleva .DeOutbound().
        var marcado = PeticionA(EndpointDeOutbound.Instancia);

        (await AutorizarAsync(Con(Roles.GestorCae), marcado, new UsuarioConPapel(PapelActivo.Inbound))).Succeeded.Should().BeFalse();
        (await AutorizarAsync(Con(Roles.GestorCae), marcado, new UsuarioConPapel(PapelActivo.Outbound))).Succeeded.Should().BeTrue();
        (await AutorizarAsync(Con(Roles.GestorCae), PeticionA(), new UsuarioConPapel(PapelActivo.Inbound))).Succeeded.Should().BeTrue();
    }

    public static TheoryData<Type> PaginasSinMarca =>
    [
        typeof(Features.Usuarios.Pages.Usuarios),
        typeof(Features.Dashboard.Pages.Inicio),
    ];

    [Theory]
    [MemberData(nameof(PaginasSinMarca))]
    public async Task Una_pagina_sin_la_marca_no_mira_el_papel(Type pagina)
    {
        var usuario = new UsuarioConPapel(PapelActivo.Inbound);

        (await AutorizarAsync(Con(Roles.Administrador), Ruta(pagina), usuario, CualquieraAutenticado)).Succeeded.Should().BeTrue(
            "el handler solo restringe las páginas marcadas");
        usuario.ConsultasDePapel.Should().Be(0, "sin marca no se consulta el papel");
    }

    [Fact]
    public async Task Sin_recurso_o_con_un_endpoint_que_no_es_una_pagina_no_niega_nada()
    {
        var usuario = new UsuarioConPapel(PapelActivo.Inbound);

        (await AutorizarAsync(Con(Roles.GestorCae), recurso: null, usuario)).Succeeded.Should().BeTrue();
        (await AutorizarAsync(Con(Roles.GestorCae), PeticionA(), usuario)).Succeeded.Should().BeTrue();
        usuario.ConsultasDePapel.Should().Be(0);
    }

    private static List<Type> Marcadas() =>
        typeof(PaginaDeOutboundAttribute).Assembly.GetTypes()
            .Where(PaginaDeOutboundPorPapelAuthorizationHandler.EsPaginaDeOutbound)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    public static TheoryData<Type> PaginasMarcadas => [.. Marcadas()];

    [Fact]
    public void Hay_paginas_marcadas()
    {
        // Control positivo de la teoría de abajo: sin páginas marcadas pasaría vacía.
        Marcadas().Should().HaveCountGreaterThanOrEqualTo(31);
    }

    [Theory]
    [MemberData(nameof(PaginasMarcadas))]
    public async Task Toda_pagina_marcada_se_niega_con_papel_Inbound_y_se_abre_con_papel_Outbound(Type pagina)
    {
        (await AutorizarAsync(Con(Roles.Administrador), Ruta(pagina), new UsuarioConPapel(PapelActivo.Inbound), CualquieraAutenticado))
            .Succeeded.Should().BeFalse();
        (await AutorizarAsync(Con(Roles.Administrador), Ruta(pagina), new UsuarioConPapel(PapelActivo.Outbound), CualquieraAutenticado))
            .Succeeded.Should().BeTrue();
    }

    private static readonly AuthorizationPolicy CualquieraAutenticado =
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    private static async Task<AuthorizationResult> AutorizarAsync(
        ClaimsPrincipal principal, object? recurso, UsuarioConPapel usuario, AuthorizationPolicy? politica = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddAuthorizationCore();
        servicios.AddSingleton<ICurrentUserService>(usuario);
        servicios.AddScoped<IAuthorizationHandler, PaginaDeOutboundPorPapelAuthorizationHandler>();

        await using var proveedor = servicios.BuildServiceProvider();
        await using var ambito = proveedor.CreateAsyncScope();
        return await ambito.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, recurso, politica ?? RolesDeMiTrabajo);
    }

    private static ClaimsPrincipal Con(string rol) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba"));

    private static Microsoft.AspNetCore.Components.RouteData Ruta(Type pagina) =>
        new(pagina, new Dictionary<string, object?>());

    private static DefaultHttpContext PeticionA(params object[] metadatos)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadatos), "prueba"));
        return http;
    }

    /// <summary>Una persona cuyo papel activo es <paramref name="papel"/>; cuenta cuántas veces se le pregunta.</summary>
    private sealed class UsuarioConPapel(PapelActivo papel) : ICurrentUserService
    {
        public int ConsultasDePapel { get; private set; }

        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(null);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);

        public Task<PapelActivo> ObtenerPapelActivoAsync()
        {
            ConsultasDePapel++;
            return Task.FromResult(papel);
        }
    }
}
