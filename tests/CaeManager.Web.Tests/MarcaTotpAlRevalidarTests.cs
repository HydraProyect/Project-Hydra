using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="SignInManagerCuentaDesactivada.ConservarMarcaTotpAlRevalidar"/>: el validador de la cookie
/// rehace el principal cada intervalo de revalidación, y la marca del TOTP de la sesión (precondición
/// de abrir una Sesión Privilegiada) se copia de la cookie validada sin rejuvenecerse ni inventarse.
/// </summary>
public class MarcaTotpAlRevalidarTests
{
    private const string Instante = "1700000000";

    [Fact]
    public async Task Copia_la_marca_de_la_cookie_validada_con_su_instante()
    {
        var contexto = Contexto(actual: Principal("cuenta", Instante), nuevo: Principal("cuenta", null));

        await Gancho()(contexto);

        contexto.NewPrincipal!.FindAll(SignInManagerCuentaDesactivada.ClaimTotpSesion)
            .Select(c => c.Value).Should().Equal([Instante]);
    }

    [Fact]
    public async Task No_la_inventa_si_la_cookie_no_la_traia()
    {
        var contexto = Contexto(actual: Principal("cuenta", null), nuevo: Principal("cuenta", null));

        await Gancho()(contexto);

        contexto.NewPrincipal!.HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimTotpSesion).Should().BeFalse();
    }

    [Fact]
    public async Task Descarta_la_marca_que_traiga_el_principal_rehecho_por_la_fabrica()
    {
        // Un claim guardado en AspNetUserClaims llegaría por la fábrica: no cuenta como TOTP.
        var contexto = Contexto(actual: Principal("cuenta", null), nuevo: Principal("cuenta", "9999999999"));

        await Gancho()(contexto);

        contexto.NewPrincipal!.HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimTotpSesion).Should().BeFalse();
    }

    [Fact]
    public async Task No_copia_la_marca_de_otra_cuenta()
    {
        var contexto = Contexto(actual: Principal("otra", Instante), nuevo: Principal("cuenta", null));

        await Gancho()(contexto);

        contexto.NewPrincipal!.HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimTotpSesion).Should().BeFalse();
    }

    [Fact]
    public async Task Respeta_el_gancho_anterior()
    {
        var anteriorLlamado = false;
        var opciones = new SecurityStampValidatorOptions
        {
            OnRefreshingPrincipal = _ => { anteriorLlamado = true; return Task.CompletedTask; },
        };
        SignInManagerCuentaDesactivada.ConservarMarcaTotpAlRevalidar(opciones);

        await opciones.OnRefreshingPrincipal!(Contexto(Principal("cuenta", Instante), Principal("cuenta", null)));

        anteriorLlamado.Should().BeTrue();
    }

    private static Func<SecurityStampRefreshingPrincipalContext, Task> Gancho()
    {
        var opciones = new SecurityStampValidatorOptions();
        SignInManagerCuentaDesactivada.ConservarMarcaTotpAlRevalidar(opciones);
        return opciones.OnRefreshingPrincipal!;
    }

    private static SecurityStampRefreshingPrincipalContext Contexto(ClaimsPrincipal actual, ClaimsPrincipal nuevo) =>
        new() { CurrentPrincipal = actual, NewPrincipal = nuevo };

    private static ClaimsPrincipal Principal(string cuenta, string? marca)
    {
        var identidad = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cuenta)], "Prueba");
        if (marca is not null)
            identidad.AddClaim(new Claim(SignInManagerCuentaDesactivada.ClaimTotpSesion, marca));
        return new ClaimsPrincipal(identidad);
    }
}
