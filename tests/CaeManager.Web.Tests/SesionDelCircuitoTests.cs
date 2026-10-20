using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class SesionDelCircuitoTests : BunitContext
{
    [Theory]
    [InlineData("/empresas/123", "/cuenta/iniciar-sesion?ReturnUrl=%2Fempresas%2F123")]
    [InlineData("/documentos?nombre=Jos%C3%A9&vista=mia#detalle", "/cuenta/iniciar-sesion?ReturnUrl=%2Fdocumentos%3Fnombre%3DJos%25C3%25A9%26vista%3Dmia%23detalle")]
    [InlineData("/", "/cuenta/iniciar-sesion?ReturnUrl=%2F")]
    [InlineData("/cuenta/iniciar-sesion", "/cuenta/iniciar-sesion")]
    [InlineData("/cuenta/cambiar-contrasena?paso=2", "/cuenta/iniciar-sesion")]
    [InlineData("/Cuenta/perfil", "/cuenta/iniciar-sesion")]
    public async Task Al_perder_la_sesion_conserva_la_ruta_salvo_en_cuenta(string ruta, string destino)
    {
        var proveedor = new ProveedorDeSesion(autenticado: true);
        Services.AddSingleton<AuthenticationStateProvider>(proveedor);
        var navegacion = Services.GetRequiredService<BunitNavigationManager>();
        navegacion.NavigateTo(ruta);
        var cut = Render<SesionDelCircuito>();

        await cut.InvokeAsync(proveedor.Invalidar);

        cut.WaitForAssertion(() => navegacion.Uri.Should().Be("http://localhost" + destino));
        navegacion.History.First().Options.ForceLoad.Should().BeTrue();
        if (!ruta.StartsWith("/cuenta/", StringComparison.OrdinalIgnoreCase))
        {
            var retorno = Uri.UnescapeDataString(new Uri(navegacion.Uri).Query.Split('=', 2)[1]);
            RedireccionLocal.Sanear(retorno).Should().Be(ruta);
        }
    }

    [Fact]
    public async Task Una_sesion_que_ya_nace_anonima_no_redirige()
    {
        var proveedor = new ProveedorDeSesion(autenticado: false);
        Services.AddSingleton<AuthenticationStateProvider>(proveedor);
        var navegacion = Services.GetRequiredService<BunitNavigationManager>();
        var cut = Render<SesionDelCircuito>();

        await cut.InvokeAsync(proveedor.Invalidar);

        navegacion.History.Should().BeEmpty();
    }

    private sealed class ProveedorDeSesion(bool autenticado) : AuthenticationStateProvider
    {
        private AuthenticationState _estado = new(new ClaimsPrincipal(
            autenticado ? new ClaimsIdentity("prueba") : new ClaimsIdentity()));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_estado);

        public void Invalidar()
        {
            _estado = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            NotifyAuthenticationStateChanged(Task.FromResult(_estado));
        }
    }
}
