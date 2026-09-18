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
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// El símbolo TALVEG de la cabecera del menú lateral (petición del propietario,
/// 2026-09-18, con capturas): va delante de <c>@Marca.Nombre</c>, no lo sustituye,
/// y es decorativo porque el nombre justo al lado ya identifica la marca ante un
/// lector de pantalla.
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
    }

    [Fact]
    public void El_simbolo_va_delante_del_nombre_de_marca_y_es_decorativo()
    {
        var cut = Render<MainLayout>();

        var marca = cut.Find("div.marca");
        var hijos = marca.Children;

        hijos.Length.Should().BeGreaterThanOrEqualTo(2);
        hijos[0].TagName.Should().Be("IMG", "el símbolo debe pintarse antes que el nombre, no al revés");
        hijos[0].GetAttribute("alt").Should().Be("",
            "el nombre \"CAE Manager\" justo al lado ya identifica la marca: un lector de pantalla no debe anunciarlo dos veces");

        marca.TextContent.Trim().Should().Be(Marca.PorDefecto,
            "la petición fue añadir el símbolo delante, no sustituir el texto");
    }

    [Fact]
    public void Las_dos_variantes_del_simbolo_estan_presentes_para_alternar_por_tema()
    {
        var cut = Render<MainLayout>();

        var imagenes = cut.FindAll("div.marca img");

        imagenes.Should().HaveCount(2, "una variante para fondo claro y otra para fondo oscuro");
        imagenes.Should().Contain(img =>
            img.ClassList.Contains("marca-simbolo-claro") && img.GetAttribute("src") == "img/marca/simbolo.svg");
        imagenes.Should().Contain(img =>
            img.ClassList.Contains("marca-simbolo-oscuro") && img.GetAttribute("src") == "img/marca/simbolo-inverso.svg");
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
