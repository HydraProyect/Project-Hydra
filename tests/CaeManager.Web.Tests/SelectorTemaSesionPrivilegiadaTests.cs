using System.Security.Claims;
using Bunit;
using CaeManager.Application.Plataforma;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// En una Sesión Privilegiada la conexión lleva <c>cae_app_soporte</c>, que no tiene <c>UPDATE</c> sobre la cuenta:
/// elegir el tema no podría guardarse, así que el interruptor no se ofrece. Sin Sesión Privilegiada sigue
/// apareciendo, y ese es el control positivo: sin él, un interruptor que nunca se pinta pasaría por «oculto».
/// </summary>
public sealed class SelectorTemaSesionPrivilegiadaTests : BunitContext
{
    private readonly Guid _usuarioId = Guid.NewGuid();

    public SelectorTemaSesionPrivilegiadaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<AuthenticationStateProvider>(new ProveedorDeUsuario(_usuarioId));
        Services.AddSingleton<IPreferenciasCuentaPropia>(_preferencias);
    }

    private readonly PreferenciasDeLectura _preferencias = new();

    [Fact]
    public void Sin_Sesion_Privilegiada_el_interruptor_de_tema_se_pinta()
    {
        Services.AddSingleton<ICoordenadaSesionPrivilegiada>(new CoordenadaSesionPrivilegiadaDePrueba(false));

        var cut = Render<SelectorTema>();

        cut.WaitForAssertion(() => cut.Find("button.selector-tema").GetAttribute("role").Should().Be("switch"));
    }

    [Fact]
    public void Con_una_Sesion_Privilegiada_seleccionada_el_interruptor_de_tema_no_se_pinta()
    {
        Services.AddSingleton<ICoordenadaSesionPrivilegiada>(new CoordenadaSesionPrivilegiadaDePrueba(true));

        var cut = Render<SelectorTema>();

        // El componente llegó a leer la cuenta: la ausencia del interruptor es por la sesión, no por no haber cargado.
        cut.WaitForAssertion(() => _preferencias.Lecturas.Should().Be(1));
        cut.FindAll("button.selector-tema").Should().BeEmpty();
        cut.FindAll(".selector-tema-envoltura").Should().BeEmpty();
        cut.Markup.Should().NotContain("No disponible", "el islote no cae en el aviso de error: el control simplemente no se ofrece");
    }

    private sealed class ProveedorDeUsuario(Guid usuarioId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString())], "prueba"))));
    }

    private sealed class PreferenciasDeLectura : IPreferenciasCuentaPropia
    {
        public int Lecturas { get; private set; }

        public Task<PreferenciasDeCuenta?> ObtenerAsync(Guid usuarioId, CancellationToken ct = default)
        {
            Lecturas++;
            return Task.FromResult<PreferenciasDeCuenta?>(new PreferenciasDeCuenta(TemaPreferido.Sistema, IdiomaPreferido.Espanol, null));
        }

        public Task<ResultadoEscrituraCuenta> GuardarTemaAsync(Guid usuarioId, TemaPreferido tema, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ResultadoEscrituraCuenta> GuardarIdiomaAsync(Guid usuarioId, IdiomaPreferido idioma, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ResultadoEscrituraCuenta> RegistrarActividadAsync(Guid usuarioId, DateTime instanteUtc, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
