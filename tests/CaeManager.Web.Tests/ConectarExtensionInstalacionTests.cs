using Bunit;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Extension;
using CaeManager.Web.Features.Extension.Pages;
using CaeManager.Application.Common;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// FS-17: <c>/cuenta/extension</c> decía «no hemos detectado la extensión» y no decía cómo instalarla. La página lleva
/// siempre la sección «Instalar la extensión»: el enlace de instalación si el entorno tiene uno decidido
/// (<c>Extension:UrlInstalacion</c>) y, mientras no lo haya, los pasos de la carga manual con la versión mínima.
/// </summary>
public class ConectarExtensionInstalacionTests : BunitContext
{
    private IRenderedComponent<ConectarExtension> Renderizar(string? urlInstalacion)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(Roles.GestorCae);
        Services.AddLocalization();
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Extension:UrlInstalacion"] = urlInstalacion })
            .Build());
        return Render<ConectarExtension>();
    }

    /// <summary>La sección de instalación no depende de generar el token: la página no llega a pedir ningún usuario.</summary>
    private sealed class AlmacenQueNadieDebeTocar : IUserStore<ApplicationUser>
    {
        private static Exception NoPrevisto() => new NotSupportedException("La sección de instalación no consulta usuarios.");

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public void Dispose() { }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sin_url_de_instalacion_decidida_la_pagina_da_los_pasos_de_la_carga_manual_con_la_version_minima(string? url)
    {
        var cut = Renderizar(url);

        var seccion = cut.Find("section.instalar-extension");
        seccion.QuerySelector("h2")!.TextContent.Should().Be("Instalar la extensión");
        seccion.QuerySelectorAll("ol.pasos-instalar-extension li").Should().HaveCount(4);
        seccion.TextContent.Should().Contain("chrome://extensions").And.Contain("Cargar descomprimida")
            .And.Contain($"versión {CompatibilidadExtension.VersionMinimaConexionManual} o posterior");
        seccion.QuerySelectorAll("a").Should().BeEmpty("no hay URL de instalación decidida: no se inventa un enlace");
    }

    [Fact]
    public void Con_url_de_instalacion_configurada_la_pagina_enlaza_a_ella_en_lugar_de_los_pasos_manuales()
    {
        var cut = Renderizar("https://example.invalid/extension");

        var enlace = cut.Find("section.instalar-extension a.enlace-instalar-extension");
        enlace.GetAttribute("href").Should().Be("https://example.invalid/extension");
        enlace.GetAttribute("rel").Should().Be("noopener noreferrer");
        cut.FindAll("ol.pasos-instalar-extension").Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://example.invalid/extension")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/extension")]
    [InlineData("no es una url")]
    public void Una_url_que_no_es_https_absoluta_no_se_enlaza_y_quedan_los_pasos_manuales(string url)
    {
        var cut = Renderizar(url);

        cut.FindAll("section.instalar-extension a").Should().BeEmpty();
        cut.FindAll("ol.pasos-instalar-extension li").Should().HaveCount(4);
    }

    [Fact]
    public void La_instalacion_esta_a_la_vista_antes_de_generar_el_token()
    {
        var cut = Renderizar(null);

        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Generar token");
        cut.FindAll("section.instalar-extension").Should().ContainSingle();
    }
}
