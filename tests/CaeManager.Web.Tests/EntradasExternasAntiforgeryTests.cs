using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Features.Extension;
using CaeManager.Web.Features.Plataforma;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

public class EntradasExternasAntiforgeryTests
{
    private const string Subida = "/extension/acreditaciones/11111111-1111-1111-1111-111111111111/subida";

    [Theory]
    [InlineData(Subida)]
    [InlineData("/cuenta/soporte/salir")]
    [InlineData("/cuenta/cerrar-sesion")]
    public async Task Los_post_con_cookie_sin_token_responden_400(string ruta)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        await cliente.GetStringAsync("/token");
        using var respuesta = await cliente.PostAsync(ruta, new FormUrlEncodedContent([]));
        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(Subida)]
    [InlineData("/cuenta/soporte/salir")]
    [InlineData("/cuenta/cerrar-sesion")]
    public async Task Los_post_vacios_sin_token_tambien_responden_400(string ruta)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        await cliente.GetStringAsync("/token");
        using var respuesta = await cliente.PostAsync(ruta, null);
        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
    [Fact]
    public async Task Una_cabecera_de_extension_invalida_no_exime_a_la_cookie_del_token()
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        await cliente.GetStringAsync("/token");
        cliente.DefaultRequestHeaders.Add("Authorization", "Extension falso");
        using var respuesta = await cliente.PostAsync(Subida, new FormUrlEncodedContent([]));
        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task La_credencial_validada_de_extension_permite_subir_sin_cookie_ni_antiforgery()
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        cliente.DefaultRequestHeaders.Add("Authorization", "Extension valido");
        using var respuesta = await cliente.PostAsync(Subida, null);
        respuesta.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData(Subida, HttpStatusCode.NoContent)]
    [InlineData("/cuenta/cerrar-sesion", HttpStatusCode.Found)]
    [InlineData("/cuenta/soporte/salir", HttpStatusCode.Unauthorized)]
    public async Task El_token_valido_permite_el_post_con_cookie(string ruta, HttpStatusCode esperado)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        var token = await cliente.GetStringAsync("/token");
        using var respuesta = await cliente.PostAsync(ruta,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        respuesta.StatusCode.Should().Be(esperado);
    }

    private static HttpClient Cliente(WebApplication app) => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
    };

    private static async Task<WebApplication> ArrancarAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddDataProtection();
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme)
            .AddScheme<AuthenticationSchemeOptions, ExtensionFalsa>(ExtensionAuthenticationSchemeOptions.NombreEsquema, _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy(Policies.SesionOExtension, p => p
            .AddAuthenticationSchemes(ExtensionAuthenticationSchemeOptions.NombreEsquema, IdentityConstants.ApplicationScheme)
            .RequireAuthenticatedUser()));
        builder.Services.AddIdentityCore<ApplicationUser>().AddUserStore<AlmacenInerte>().AddSignInManager();
        builder.Services.AddSingleton<IMediator>(new Mediador());
        builder.Services.AddSingleton<ICurrentUserService>(new UsuarioSinSesionSoporte());
        builder.Services.AddSingleton<IClienteActivoSeleccionado>(new SeleccionVacia());
        builder.Services.AddSingleton<IEmailService>(_ => null!);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapGet("/token", async (HttpContext contexto, IAntiforgery antiforgery) =>
        {
            contexto.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.Empty.ToString())], IdentityConstants.ApplicationScheme));
            await contexto.SignInAsync(IdentityConstants.ApplicationScheme, contexto.User);
            return antiforgery.GetAndStoreTokens(contexto).RequestToken!;
        });
        app.MapIdentityEndpoints();
        app.MapSesionSoporteEndpoints();
        app.MapMarcarAcreditacionSubidaEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class UsuarioSinSesionSoporte : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(null);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(null);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }

    private sealed class SeleccionVacia : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
    private sealed class ExtensionFalsa(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.Authorization == "Extension valido"
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Guid.Empty.ToString())], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    private sealed class AlmacenInerte : IUserStore<ApplicationUser>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser user, string? name, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByIdAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string name, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Mediador : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)Result.Exito());
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }
}