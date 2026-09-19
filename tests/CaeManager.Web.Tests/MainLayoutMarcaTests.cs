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
        Services.AddSingleton(new EstadoDelCircuito());
        Services.AddSingleton(new PuertaAccesoDatos());
        Services.AddSingleton(usuarios);
        Services.AddSingleton(new ActividadUsuarioService(null!, usuarios, new PuertaAccesoDatos()));
        Services.AddSingleton<AuthenticationStateProvider>(new ProveedorAnonimo());

        // Solo para que MainLayout resuelva la inyección: con un usuario
        // anónimo OnParametersSetAsync vuelve antes de llegar al catch que lo
        // usa (ver MainLayoutFallaCerradoTests para ese camino).
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
    }

    [Fact]
    public void El_wordmark_sustituye_al_texto_y_la_etiqueta_CAE_va_a_su_lado()
    {
        var cut = Render<MainLayout>();

        var marca = cut.Find("div.marca");

        marca.TextContent.Trim().Should().Be("CAE",
            "el texto \"CAE Manager\" se sustituye por el wordmark: solo debe quedar texto real en la etiqueta");
        marca.TextContent.Should().NotContain(Marca.PorDefecto,
            "la opción D reemplaza el nombre en texto por el wordmark, a diferencia de la opción del símbolo");

        var wordmarks = marca.QuerySelectorAll("img.marca-wordmark");
        wordmarks.Should().HaveCount(2);
        wordmarks.Should().OnlyContain(img => img.GetAttribute("alt") == "TALVEG",
            "sin un texto de marca al lado, el wordmark ya no es decorativo: debe anunciarse");

        var etiqueta = cut.Find("span.marca-etiqueta");
        etiqueta.TextContent.Should().Be("CAE", "la etiqueta es texto real, no una imagen");
    }

    [Fact]
    public void Las_dos_variantes_del_wordmark_estan_presentes_para_alternar_por_tema()
    {
        var cut = Render<MainLayout>();

        var imagenes = cut.FindAll("div.marca img");

        imagenes.Should().HaveCount(2, "una variante para fondo claro y otra para fondo oscuro");
        imagenes.Should().Contain(img =>
            img.ClassList.Contains("marca-wordmark-claro") && img.GetAttribute("src") == "img/marca/talveg-lockup-claro.svg");
        imagenes.Should().Contain(img =>
            img.ClassList.Contains("marca-wordmark-oscuro") && img.GetAttribute("src") == "img/marca/talveg-lockup-oscuro.svg");
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
