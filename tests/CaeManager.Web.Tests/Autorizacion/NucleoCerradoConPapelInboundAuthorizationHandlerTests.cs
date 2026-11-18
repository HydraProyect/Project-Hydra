using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Extension;
using CaeManager.Web.Services.Autorizacion;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests.Autorizacion;

/// <summary>
/// Decisión O-1 (b): con papel Inbound, el Núcleo y Facturación quedan cerrados por defecto, salvo lo abierto de forma
/// explícita. Se niega solo con papel Inbound: el Tenant de origen, el papel Outbound y la Sesión Privilegiada (sin
/// papel) no pierden nada, y una operación que ya no autoriza tampoco cierra el Núcleo, porque la retira la revalidación
/// del cliente activo en la misma petición (a diferencia de <see cref="CriterioDePapel"/>, que sí la niega en la guarda
/// de comandos y en las páginas de Outbound). También la extensión: con la sesión web y papel Inbound no se emite su
/// token ni responden las dos rutas que aceptan la sesión.
/// </summary>
public class NucleoCerradoConPapelInboundAuthorizationHandlerTests
{
    private static readonly Type Usuarios = typeof(Features.Usuarios.Pages.Usuarios);
    private static readonly Type Trabajadores = typeof(Features.Trabajadores.Pages.Trabajadores);
    private static readonly Type CambiarContrasena = typeof(Components.Account.Pages.CambiarContrasena);
    private static readonly Type MiTrabajo = typeof(Features.Bandeja.Pages.MiTrabajo);
    private static readonly Type CentrosInbound = typeof(Features.Inbound.CentroInbound.Pages.CentrosInbound);

    [Theory]
    [InlineData(PapelActivo.Inbound, false)]
    [InlineData(PapelActivo.OperacionQueYaNoAutoriza, true)]
    [InlineData(PapelActivo.Outbound, true)]
    [InlineData(PapelActivo.TenantDeOrigen, true)]
    [InlineData(PapelActivo.SinPapel, true)]
    public async Task Una_pagina_cerrada_del_Nucleo_se_niega_solo_con_papel_Inbound(PapelActivo papel, bool entra)
    {
        var usuario = new UsuarioConPapel(papel);

        (await AutorizarAsync(Ruta(Usuarios), usuario)).Succeeded.Should().Be(entra, "el rol cumple; decide el papel {0}", papel);
        usuario.ConsultasDePapel.Should().Be(1);

        // El primer render por HTTP: el middleware evalúa el endpoint de la página con el HttpContext como recurso.
        (await AutorizarAsync(PeticionA(new ComponentTypeMetadata(Usuarios)), new UsuarioConPapel(papel))).Succeeded.Should().Be(entra);
    }

    /// <summary>
    /// El caso de <c>PropuestaApoyoCarteraE2ETests</c>: tras desasignarse, la cookie del Tenant seleccionado lleva una
    /// operación que ya no autoriza hasta que <c>RevalidacionClienteActivoMiddleware</c>, que corre después de
    /// <c>UseAuthorization</c>, la retira en esa misma petición. Trabajadores (<c>[Authorize]</c> sin roles, del Núcleo y
    /// sin marca) tiene que pasar la autorización, por HTTP y en el router, para que la revalidación devuelva la sesión al
    /// Tenant de origen y la página se pinte allí, como antes de O-1 (b); negarla aquí mandaba a «Acceso denegado». Con
    /// papel Inbound sigue cerrada, y una página de Outbound marcada sigue negando ese estado (su criterio no cambia).
    /// </summary>
    [Fact]
    public async Task Una_operacion_que_ya_no_autoriza_no_cierra_el_Nucleo_y_el_papel_Inbound_si()
    {
        NucleoCerradoConPapelInboundAuthorizationHandler.SeCierraConPapelInbound(Trabajadores)
            .Should().BeTrue("Trabajadores es del Núcleo y no está abierta: el papel se mira");

        foreach (var recurso in new object[] { Ruta(Trabajadores), PeticionA(new ComponentTypeMetadata(Trabajadores)) })
        {
            var caducada = new UsuarioConPapel(PapelActivo.OperacionQueYaNoAutoriza);
            (await AutorizarAsync(recurso, caducada)).Succeeded
                .Should().BeTrue("la selección caducada la retira la revalidación, que corre después de la autorización");
            caducada.ConsultasDePapel.Should().Be(1, "el handler sí consulta el papel de una página que cierra");

            (await AutorizarAsync(recurso, new UsuarioConPapel(PapelActivo.Inbound))).Succeeded
                .Should().BeFalse("con papel Inbound el Núcleo sigue cerrado (O-1 (b))");
        }

        (await AutorizarAsync(Ruta(MiTrabajo), new UsuarioConPapel(PapelActivo.OperacionQueYaNoAutoriza))).Succeeded
            .Should().BeFalse("las páginas de Outbound siguen el criterio de la guarda de comandos");
    }

    [Theory]
    [InlineData(PapelActivo.Inbound)]
    [InlineData(PapelActivo.Outbound)]
    [InlineData(PapelActivo.TenantDeOrigen)]
    public async Task Una_pagina_abierta_se_permite_con_cualquier_papel_sin_consultarlo(PapelActivo papel)
    {
        var usuario = new UsuarioConPapel(papel);

        (await AutorizarAsync(Ruta(CambiarContrasena), usuario)).Succeeded.Should().BeTrue();
        (await AutorizarAsync(PeticionA(new ComponentTypeMetadata(CambiarContrasena)), usuario)).Succeeded.Should().BeTrue();
        usuario.ConsultasDePapel.Should().Be(0);
    }

    [Fact]
    public async Task Las_pantallas_Inbound_y_las_de_Outbound_marcadas_no_son_de_este_handler()
    {
        // Inbound se autoriza con IAutorizacionInbound; Outbound marcada, con PaginaDeOutboundPorPapelAuthorizationHandler.
        var usuario = new UsuarioConPapel(PapelActivo.Inbound);

        (await AutorizarAsync(Ruta(CentrosInbound), usuario)).Succeeded.Should().BeTrue();
        usuario.ConsultasDePapel.Should().Be(0);

        var deOutbound = new UsuarioConPapel(PapelActivo.Inbound);
        (await AutorizarAsync(Ruta(MiTrabajo), deOutbound)).Succeeded.Should().BeFalse("la niega el handler de Outbound");
        deOutbound.ConsultasDePapel.Should().Be(1, "el papel se consulta una sola vez: solo el handler de Outbound");
    }

    [Theory]
    [InlineData(PapelActivo.Inbound, false)]
    [InlineData(PapelActivo.OperacionQueYaNoAutoriza, true)]
    [InlineData(PapelActivo.Outbound, true)]
    [InlineData(PapelActivo.TenantDeOrigen, true)]
    [InlineData(PapelActivo.SinPapel, true)]
    public async Task Un_endpoint_del_grupo_del_Nucleo_se_cierra_con_papel_Inbound_salvo_si_esta_abierto(PapelActivo papel, bool entra)
    {
        (await AutorizarAsync(PeticionA(EndpointDelNucleoOFacturacion.Instancia), new UsuarioConPapel(papel)))
            .Succeeded.Should().Be(entra);

        var abierto = new UsuarioConPapel(papel);
        (await AutorizarAsync(PeticionA(EndpointDelNucleoOFacturacion.Instancia, EndpointAbiertoConPapelInbound.Instancia), abierto))
            .Succeeded.Should().BeTrue();
        abierto.ConsultasDePapel.Should().Be(0);
    }

    [Fact]
    public async Task Sin_recurso_o_con_un_endpoint_fuera_del_grupo_no_niega_nada()
    {
        var usuario = new UsuarioConPapel(PapelActivo.Inbound);

        (await AutorizarAsync(recurso: null, usuario)).Succeeded.Should().BeTrue();
        (await AutorizarAsync(PeticionA(), usuario)).Succeeded.Should().BeTrue();
        (await AutorizarAsync(new DefaultHttpContext(), usuario)).Succeeded.Should().BeTrue();
        usuario.ConsultasDePapel.Should().Be(0);
    }

    [Fact]
    public async Task El_grupo_de_prefijo_vacio_cierra_sus_endpoints_reales_sin_cambiar_la_ruta()
    {
        // Lo mismo que hace Program.cs: el metadato del grupo llega a cada endpoint, y la ruta no cambia.
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var grupo = app.MapGroup(string.Empty).DelNucleoOFacturacion();
        grupo.MapGet("/cerrada", () => "x");
        grupo.MapGet("/abierta", () => "x").AbiertoConPapelInbound();
        app.MapGet("/fuera", () => "x");

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .ToDictionary(e => e.RoutePattern.RawText!);
        endpoints.Keys.Should().BeEquivalentTo(["/cerrada", "/abierta", "/fuera"]);

        (await AutorizarAsync(PeticionA(endpoints["/cerrada"]), new UsuarioConPapel(PapelActivo.Inbound))).Succeeded.Should().BeFalse();
        (await AutorizarAsync(PeticionA(endpoints["/abierta"]), new UsuarioConPapel(PapelActivo.Inbound))).Succeeded.Should().BeTrue();
        (await AutorizarAsync(PeticionA(endpoints["/fuera"]), new UsuarioConPapel(PapelActivo.Inbound))).Succeeded.Should().BeTrue();
        (await AutorizarAsync(PeticionA(endpoints["/cerrada"]), new UsuarioConPapel(PapelActivo.Outbound))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Los_endpoints_de_la_extension_que_aceptan_la_sesion_web_se_niegan_con_papel_Inbound()
    {
        // Los endpoints reales, con sus metadatos: el token, la lista de acreditaciones y «Lo he enviado» (y deshacer).
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IMediator>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<UserManager<ApplicationUser>>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IDataProtectionProvider>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IRegistroSesionesUsuario>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<ISelloSeguridadCuenta>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IClienteActivoSeleccionado>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<ISesionOrigenExtension>(_ => throw new NotSupportedException());
        await using var app = builder.Build();
        app.MapExtensionTokenEndpoints();
        app.MapAcreditacionesPendientesEndpoints();
        app.MapMarcarAcreditacionSubidaEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        endpoints.Select(e => e.RoutePattern.RawText).Should().BeEquivalentTo(
        [
            "/cuenta/extension/token",
            "/extension/acreditaciones-pendientes",
            "/extension/acreditaciones/{id:guid}/subida",
            "/extension/acreditaciones/{id:guid}/subida/deshacer",
        ]);

        foreach (var endpoint in endpoints)
        {
            (await AutorizarAsync(PeticionA(endpoint), new UsuarioConPapel(PapelActivo.Inbound))).Succeeded
                .Should().BeFalse($"{endpoint.RoutePattern.RawText} con la sesión web y papel Inbound");
            (await AutorizarAsync(PeticionA(endpoint), new UsuarioConPapel(PapelActivo.Outbound))).Succeeded
                .Should().BeTrue($"{endpoint.RoutePattern.RawText} con papel Outbound");
            (await AutorizarAsync(PeticionA(endpoint), new UsuarioConPapel(PapelActivo.TenantDeOrigen))).Succeeded
                .Should().BeTrue($"{endpoint.RoutePattern.RawText} con el Tenant de origen");
        }
    }

    private static readonly AuthorizationPolicy CualquieraAutenticado =
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    /// <summary>La composición de Program.cs: los dos handlers de papel, con ámbito.</summary>
    private static async Task<AuthorizationResult> AutorizarAsync(object? recurso, UsuarioConPapel usuario)
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddAuthorizationCore();
        servicios.AddSingleton<ICurrentUserService>(usuario);
        servicios.AddScoped<IAuthorizationHandler, PaginaDeOutboundPorPapelAuthorizationHandler>();
        servicios.AddScoped<IAuthorizationHandler, NucleoCerradoConPapelInboundAuthorizationHandler>();

        await using var proveedor = servicios.BuildServiceProvider();
        await using var ambito = proveedor.CreateAsyncScope();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.Administrador)], "prueba"));
        return await ambito.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, recurso, CualquieraAutenticado);
    }

    private static Microsoft.AspNetCore.Components.RouteData Ruta(Type pagina) =>
        new(pagina, new Dictionary<string, object?>());

    private static DefaultHttpContext PeticionA(params object[] metadatos)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadatos), "prueba"));
        return http;
    }

    private static DefaultHttpContext PeticionA(Endpoint endpoint)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(endpoint);
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
