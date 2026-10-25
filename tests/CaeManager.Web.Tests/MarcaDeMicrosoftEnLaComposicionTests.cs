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

    private sealed class EntornoFalso : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CaeManager";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
