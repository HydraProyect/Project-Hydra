using System.Security.Claims;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// El gancho que conserva la marca de inicio por Microsoft al revalidar la cookie
/// (<see cref="RestriccionLoginLocalClaimsTransformation.ConservarMetodoLoginAlRevalidar"/>) está en la
/// composición real de <c>AddInfrastructure</c>, no solo en los tests que lo registran a mano.
/// </summary>
public class MarcaDeMicrosoftEnLaComposicionTests
{
    [Fact]
    public async Task La_composicion_real_conserva_la_marca_de_Microsoft_al_revalidar_la_cookie()
    {
        var servicios = new ServiceCollection();
        servicios.AddInfrastructure(new ConfigurationBuilder().Build(), new EntornoFalso());
        using var proveedor = servicios.BuildServiceProvider();
        var opciones = proveedor.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value;

        var contexto = new SecurityStampRefreshingPrincipalContext
        {
            CurrentPrincipal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(
                RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin, RestriccionLoginLocalClaimsTransformation.MetodoLoginSso)], "Prueba")),
            NewPrincipal = new ClaimsPrincipal(new ClaimsIdentity("Prueba")),
        };
        opciones.OnRefreshingPrincipal.Should().NotBeNull();
        await opciones.OnRefreshingPrincipal!(contexto);

        contexto.NewPrincipal!.HasClaim(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin,
            RestriccionLoginLocalClaimsTransformation.MetodoLoginSso).Should().BeTrue();
    }

    /// <summary>
    /// La sesión que Identity emite tras el TOTP de TALVEG del inicio con Microsoft (D-2)
    /// lleva la marca que pone <see cref="SignInManagerCuentaDesactivada.ConMarcaDeInicioConMicrosoft"/>,
    /// y el gancho de la composición real la conserva al revalidar la cookie.
    /// </summary>
    [Fact]
    public async Task La_marca_de_la_sesion_tras_el_TOTP_del_inicio_con_Microsoft_sobrevive_a_la_revalidacion()
    {
        var servicios = new ServiceCollection();
        servicios.AddInfrastructure(new ConfigurationBuilder().Build(), new EntornoFalso());
        using var proveedor = servicios.BuildServiceProvider();
        var opciones = proveedor.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value;

        var trasElTotp = SignInManagerCuentaDesactivada.ConMarcaDeInicioConMicrosoft(
            [new Claim("amr", "mfa"), new Claim(ClaimTypes.AuthenticationMethod, AzureAdOptions.Esquema)]);
        var contexto = new SecurityStampRefreshingPrincipalContext
        {
            CurrentPrincipal = new ClaimsPrincipal(new ClaimsIdentity(trasElTotp, "Prueba")),
            NewPrincipal = new ClaimsPrincipal(new ClaimsIdentity("Prueba")),
        };
        await opciones.OnRefreshingPrincipal!(contexto);

        contexto.NewPrincipal!.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin)
            .Should().ContainSingle(c => c.Value == RestriccionLoginLocalClaimsTransformation.MetodoLoginSso);
    }

    /// <summary>
    /// La marca del TOTP de la sesión (precondición de abrir una Sesión Privilegiada) también sobrevive
    /// a la revalidación en la composición real, con su instante original.
    /// </summary>
    [Fact]
    public async Task La_composicion_real_conserva_la_marca_del_TOTP_al_revalidar_la_cookie()
    {
        var servicios = new ServiceCollection();
        servicios.AddInfrastructure(new ConfigurationBuilder().Build(), new EntornoFalso());
        using var proveedor = servicios.BuildServiceProvider();
        var opciones = proveedor.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value;

        var contexto = new SecurityStampRefreshingPrincipalContext
        {
            CurrentPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "cuenta"), new Claim(SignInManagerCuentaDesactivada.ClaimTotpSesion, "1700000000")], "Prueba")),
            NewPrincipal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "cuenta")], "Prueba")),
        };
        await opciones.OnRefreshingPrincipal!(contexto);

        contexto.NewPrincipal!.FindAll(SignInManagerCuentaDesactivada.ClaimTotpSesion)
            .Select(c => c.Value).Should().Equal(["1700000000"]);
    }

    /// <summary>
    /// La marca del inicio de sesión (autenticación reciente del alta del TOTP, ASVS 4.0.3 V3.7.1)
    /// sobrevive a la revalidación en la composición real, con su instante original y junto a la del TOTP.
    /// </summary>
    [Fact]
    public async Task La_composicion_real_conserva_la_marca_del_inicio_de_sesion_al_revalidar_la_cookie()
    {
        var servicios = new ServiceCollection();
        servicios.AddInfrastructure(new ConfigurationBuilder().Build(), new EntornoFalso());
        using var proveedor = servicios.BuildServiceProvider();
        var opciones = proveedor.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value;

        var contexto = new SecurityStampRefreshingPrincipalContext
        {
            CurrentPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "cuenta"),
                    new Claim(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion, "1700000000"),
                    new Claim(SignInManagerCuentaDesactivada.ClaimTotpSesion, "1700000100"),
                ], "Prueba")),
            NewPrincipal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "cuenta")], "Prueba")),
        };
        await opciones.OnRefreshingPrincipal!(contexto);

        contexto.NewPrincipal!.FindAll(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion)
            .Select(c => c.Value).Should().Equal(["1700000000"]);
        contexto.NewPrincipal!.FindAll(SignInManagerCuentaDesactivada.ClaimTotpSesion)
            .Select(c => c.Value).Should().Equal(["1700000100"]);
    }

    private sealed class EntornoFalso : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CaeManager";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
