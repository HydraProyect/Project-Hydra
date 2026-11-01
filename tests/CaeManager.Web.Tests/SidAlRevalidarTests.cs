using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="SignInManagerCuentaDesactivada.ConservarSidAlRevalidar"/>: el validador de la cookie rehace
/// el principal cada intervalo de revalidación. Si el identificador de sesión se perdiera ahí, la cookie
/// renovada saldría sin él y la sesión moriría en la revalidación siguiente (el primer minuto).
/// </summary>
public class SidAlRevalidarTests
{
    private static readonly string Sid = Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Conserva_el_sid_de_la_cookie_validada_al_rehacer_el_principal()
    {
        var contexto = Contexto(actual: Principal("cuenta", Sid), nuevo: Principal("cuenta", null));

        await Gancho()(contexto);

        contexto.NewPrincipal!.FindAll(SignInManagerCuentaDesactivada.ClaimSid)
            .Select(c => c.Value).Should().Equal([Sid]);
        SignInManagerCuentaDesactivada.LeerSid(contexto.NewPrincipal).Should().Be(Guid.Parse(Sid));
    }

    [Fact]
    public async Task No_lo_inventa_si_la_cookie_no_lo_traia()
    {
        var contexto = Contexto(actual: Principal("cuenta", null), nuevo: Principal("cuenta", null));

        await Gancho()(contexto);

        SignInManagerCuentaDesactivada.LeerSid(contexto.NewPrincipal).Should().BeNull();
    }

    [Fact]
    public async Task Descarta_el_sid_que_traiga_el_principal_rehecho_por_la_fabrica()
    {
        var contexto = Contexto(actual: Principal("cuenta", Sid), nuevo: Principal("cuenta", Guid.NewGuid().ToString("N")));

        await Gancho()(contexto);

        contexto.NewPrincipal!.FindAll(SignInManagerCuentaDesactivada.ClaimSid)
            .Select(c => c.Value).Should().Equal([Sid]);
    }

    [Fact]
    public async Task No_copia_el_sid_de_otra_cuenta()
    {
        var contexto = Contexto(actual: Principal("otra", Sid), nuevo: Principal("cuenta", null));

        await Gancho()(contexto);

        SignInManagerCuentaDesactivada.LeerSid(contexto.NewPrincipal).Should().BeNull();
    }

    [Fact]
    public async Task Respeta_el_gancho_anterior()
    {
        var anteriorLlamado = false;
        var opciones = new SecurityStampValidatorOptions
        {
            OnRefreshingPrincipal = _ => { anteriorLlamado = true; return Task.CompletedTask; },
        };
        SignInManagerCuentaDesactivada.ConservarSidAlRevalidar(opciones);

        await opciones.OnRefreshingPrincipal!(Contexto(Principal("cuenta", Sid), Principal("cuenta", null)));

        anteriorLlamado.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-es-un-guid")]
    [InlineData("00000000000000000000000000000000")]
    public void LeerSid_rechaza_lo_que_no_es_un_identificador(string? valor) =>
        SignInManagerCuentaDesactivada.LeerSid(Principal("cuenta", valor)).Should().BeNull();

    private static Func<SecurityStampRefreshingPrincipalContext, Task> Gancho()
    {
        var opciones = new SecurityStampValidatorOptions();
        SignInManagerCuentaDesactivada.ConservarSidAlRevalidar(opciones);
        return opciones.OnRefreshingPrincipal!;
    }

    private static SecurityStampRefreshingPrincipalContext Contexto(ClaimsPrincipal actual, ClaimsPrincipal nuevo) =>
        new() { CurrentPrincipal = actual, NewPrincipal = nuevo };

    internal static ClaimsPrincipal Principal(string cuenta, string? sid)
    {
        var identidad = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cuenta)], "Prueba");
        if (sid is not null)
            identidad.AddClaim(new Claim(SignInManagerCuentaDesactivada.ClaimSid, sid));
        return new ClaimsPrincipal(identidad);
    }
}
