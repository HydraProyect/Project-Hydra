using System.Globalization;
using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Tests;

/// <summary>
/// La marca de inicio de sesión (<see cref="SignInManagerCuentaDesactivada.ClaimAutenticacionSesion"/>)
/// que el alta del TOTP lee como autenticación reciente (ASVS 4.0.3 V3.7.1): su ventana, y que la
/// revalidación periódica de la cookie la copia sin rejuvenecerla ni inventarla.
/// </summary>
public class MarcaAutenticacionRecienteTests
{
    private const string Instante = "1700000000";

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(12 * 60, false)]
    [InlineData(-4, true)]
    [InlineData(-6, false)]
    public void Solo_es_reciente_dentro_de_la_ventana(int minutosDesdeElInicio, bool esperado)
    {
        var ahora = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var marca = ahora.AddMinutes(-minutosDesdeElInicio).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        SignInManagerCuentaDesactivada.AutenticacionEsReciente(Principal("cuenta", marca), ahora).Should().Be(esperado);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-es-un-instante")]
    [InlineData("-5")]
    public void Sin_una_marca_legible_no_es_reciente(string? marca)
    {
        SignInManagerCuentaDesactivada.AutenticacionEsReciente(Principal("cuenta", marca), DateTimeOffset.UtcNow)
            .Should().BeFalse();
    }

    [Fact]
    public void La_marca_del_TOTP_no_cuenta_como_inicio_de_sesion_reciente()
    {
        var ahora = DateTimeOffset.UtcNow;
        var identidad = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "cuenta")], "Prueba");
        identidad.AddClaim(new Claim(SignInManagerCuentaDesactivada.ClaimTotpSesion,
            ahora.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

        SignInManagerCuentaDesactivada.AutenticacionEsReciente(new ClaimsPrincipal(identidad), ahora).Should().BeFalse();
    }

    [Fact]
    public async Task Al_revalidar_copia_la_marca_de_la_cookie_validada_con_su_instante()
    {
        var contexto = Contexto(actual: Principal("cuenta", Instante), nuevo: Principal("cuenta", null));

        await Gancho()(contexto);

        contexto.NewPrincipal!.FindAll(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion)
            .Select(c => c.Value).Should().Equal([Instante]);
    }

    [Fact]
    public async Task Al_revalidar_no_la_inventa_si_la_cookie_no_la_traia()
    {
        var contexto = Contexto(actual: Principal("cuenta", null), nuevo: Principal("cuenta", null));

        await Gancho()(contexto);

        contexto.NewPrincipal!.HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimAutenticacionSesion).Should().BeFalse();
    }

    [Fact]
    public async Task Al_revalidar_descarta_la_marca_que_traiga_el_principal_rehecho()
    {
        var contexto = Contexto(actual: Principal("cuenta", null), nuevo: Principal("cuenta", "9999999999"));

        await Gancho()(contexto);

        contexto.NewPrincipal!.HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimAutenticacionSesion).Should().BeFalse();
    }

    [Fact]
    public async Task Al_revalidar_no_copia_la_marca_de_otra_cuenta()
    {
        var contexto = Contexto(actual: Principal("otra", Instante), nuevo: Principal("cuenta", null));

        await Gancho()(contexto);

        contexto.NewPrincipal!.HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimAutenticacionSesion).Should().BeFalse();
    }

    [Fact]
    public async Task Al_revalidar_respeta_el_gancho_anterior()
    {
        var anteriorLlamado = false;
        var opciones = new SecurityStampValidatorOptions
        {
            OnRefreshingPrincipal = _ => { anteriorLlamado = true; return Task.CompletedTask; },
        };
        SignInManagerCuentaDesactivada.ConservarMarcaAutenticacionAlRevalidar(opciones);

        await opciones.OnRefreshingPrincipal!(Contexto(Principal("cuenta", Instante), Principal("cuenta", null)));

        anteriorLlamado.Should().BeTrue();
    }

    private static Func<SecurityStampRefreshingPrincipalContext, Task> Gancho()
    {
        var opciones = new SecurityStampValidatorOptions();
        SignInManagerCuentaDesactivada.ConservarMarcaAutenticacionAlRevalidar(opciones);
        return opciones.OnRefreshingPrincipal!;
    }

    private static SecurityStampRefreshingPrincipalContext Contexto(ClaimsPrincipal actual, ClaimsPrincipal nuevo) =>
        new() { CurrentPrincipal = actual, NewPrincipal = nuevo };

    private static ClaimsPrincipal Principal(string cuenta, string? marca)
    {
        var identidad = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cuenta)], "Prueba");
        if (marca is not null)
            identidad.AddClaim(new Claim(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion, marca));
        return new ClaimsPrincipal(identidad);
    }
}
