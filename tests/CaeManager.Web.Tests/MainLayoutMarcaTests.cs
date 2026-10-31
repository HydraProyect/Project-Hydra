using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cabecera del menú lateral, opción D elegida por el propietario (2026-09-19)
/// entre seis maquetas: el wordmark TALVEG sustituye al texto "CAE Manager", con
/// una etiqueta "CAE" a su lado. <c>Marca.Nombre</c> no se toca (sigue siendo el
/// nombre de títulos de pestaña y correos) — este cambio es solo lo que se pinta
/// en <c>div.marca</c>.
///
/// <para>
/// Sin usuario autenticado, <c>MainLayout.OnParametersSetAsync</c> vuelve antes
/// de tocar <c>UserManager</c>/<c>PuertaAccesoDatos</c>/<c>ActividadUsuarioService</c>
/// (ver <see cref="MainLayoutFallaCerradoTests"/> para el guard en sí): esas
/// dependencias solo necesitan poder resolverse por inyección aquí, nunca se
/// ejercitan de verdad.
/// </para>
/// </summary>
public class MainLayoutMarcaTests : BunitContext
{
    public MainLayoutMarcaTests()
    {
        // Solo MainLayout se monta de verdad: mismo patrón que
        // MainLayoutFallaCerradoTests, necesario porque su markup cuelga de
        // NavMenu y del resto de componentes del layout.
        ComponentFactories.Add(new TodoMenosElLayout());

        var usuarios = CrearUsuariosSinAlmacen();
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddLocalization();
        Services.AddSingleton(new EstadoDelCircuito());
        Services.AddSingleton(new PuertaAccesoDatos());
        Services.AddSingleton(usuarios);
        Services.AddSingleton<ILecturaCuentaPropia>(new LecturaCuentaPropia(null!));
        Services.AddSingleton(new ActividadUsuarioService(null!, usuarios, new PuertaAccesoDatos()));
        Services.AddSingleton<AuthenticationStateProvider>(new ProveedorAnonimo());

        // Solo para que MainLayout resuelva la inyección: con un usuario
        // anónimo OnParametersSetAsync vuelve antes de llegar al catch que lo
        // usa (ver MainLayoutFallaCerradoTests para ese camino).
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
    }

    [Fact]
    public void La_marca_es_texto_real_con_la_V_resaltada_y_la_etiqueta_CAE_va_a_su_lado()
    {
        var cut = Render<MainLayout>();

        var marca = cut.Find("div.marca");

        string.Join(' ', marca.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Should().Be("TALVEG CAE",
            "marco de Design (DDL-083): TALVEG en texto real y la etiqueta CAE; ya no hay wordmark en imagen");
        marca.QuerySelectorAll("img").Should().BeEmpty("la marca no es una imagen");

        var texto = cut.Find("div.marca span.marca-wordmark");
        texto.TextContent.Should().Be("TALVEG");
        texto.QuerySelector("b")!.TextContent.Should().Be("V", "la V va resaltada, como en marco-talveg.js");

        var etiqueta = cut.Find("span.marca-etiqueta");
        etiqueta.TextContent.Should().Be("CAE", "la etiqueta es texto real, no una imagen");
    }

    [Fact]
    public void La_marca_es_un_enlace_a_Inicio_con_el_wordmark_y_la_etiqueta_dentro()
    {
        var cut = Render<MainLayout>();

        var enlace = cut.Find("div.marca > a.marca-enlace");
        enlace.GetAttribute("href").Should().Be("/", "marco de Design: la marca lleva a Inicio");
        enlace.QuerySelector("span.marca-wordmark").Should().NotBeNull();
        enlace.QuerySelector("span.marca-etiqueta").Should().NotBeNull();
    }

    private static UserManager<ApplicationUser> CrearUsuariosSinAlmacen() => new(
        new AlmacenSinUso(), Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance);

    /// <summary>Monta MainLayout de verdad y sustituye por un stub todo lo demás.</summary>
    private sealed class TodoMenosElLayout : IComponentFactory
    {
        public bool CanCreate(Type componentType) => componentType != typeof(MainLayout);

        public IComponent Create(Type componentType) =>
            (IComponent)Activator.CreateInstance(typeof(Stub<>).MakeGenericType(componentType))!;
    }

    /// <summary>Sin autenticar: el guard de MainLayout vuelve en su primera línea.</summary>
    private sealed class ProveedorAnonimo : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>Solo existe para que UserManager se pueda construir; con el usuario anónimo nunca se llama.</summary>
    private sealed class AlmacenSinUso : IUserStore<ApplicationUser>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => throw new NotSupportedException();
    }
}
