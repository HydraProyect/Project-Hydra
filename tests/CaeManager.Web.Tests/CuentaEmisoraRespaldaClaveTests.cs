using CaeManager.Infrastructure.Identity;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// M-35: la regla de <see cref="ApplicationUser.AdmiteClaveEmitidaEn"/>, con lo que deja una cuenta
/// real al pasar por <c>Desactivar</c> y <c>Reactivar</c>. Que esos datos lleguen hasta la
/// autenticación bajo <c>cae_app_runtime</c> lo mide <c>ApiPublicaBajoRolRuntimeTests</c>.
/// </summary>
public class CuentaEmisoraRespaldaClaveTests
{
    private static readonly DateTime Emision = DateTime.UtcNow.AddDays(-10);

    private static bool Admite(ApplicationUser cuenta, DateTime emitidaEnUtc) => ApplicationUser.AdmiteClaveEmitidaEn(
        cuenta.LockoutEnd, cuenta.UltimaDesactivacionEnUtc, emitidaEnUtc, DateTimeOffset.UtcNow);

    [Fact]
    public void La_cuenta_que_nunca_se_desactivo_respalda_la_clave() =>
        Admite(new ApplicationUser(), Emision).Should().BeTrue();

    [Fact]
    public void La_cuenta_desactivada_no_respalda_la_clave()
    {
        var cuenta = new ApplicationUser();
        cuenta.Desactivar();

        Admite(cuenta, Emision).Should().BeFalse();
    }

    [Fact]
    public void Reactivar_no_devuelve_la_clave_emitida_antes_de_desactivar()
    {
        var cuenta = new ApplicationUser();
        cuenta.Desactivar();
        cuenta.Reactivar();

        cuenta.LockoutEnd.Should().BeNull("control: la cuenta está reactivada de verdad");
        Admite(cuenta, Emision).Should().BeFalse();
    }

    [Fact]
    public void La_clave_emitida_despues_de_reactivar_vale()
    {
        var cuenta = new ApplicationUser();
        cuenta.Desactivar();
        cuenta.Reactivar();

        Admite(cuenta, cuenta.UltimaDesactivacionEnUtc!.Value.AddSeconds(1)).Should().BeTrue();
    }

    // Una emisión que corre a la vez que la desactivación puede quedar fechada después de ella.
    [Fact]
    public void Con_la_cuenta_desactivada_tampoco_vale_una_clave_fechada_despues()
    {
        var cuenta = new ApplicationUser();
        cuenta.Desactivar();

        Admite(cuenta, cuenta.UltimaDesactivacionEnUtc!.Value.AddSeconds(1)).Should().BeFalse();
    }

    // Quien conociera un correo no debe poder tumbar la integración con cinco contraseñas erróneas.
    [Fact]
    public void El_bloqueo_temporal_por_intentos_fallidos_no_corta_la_clave()
    {
        var cuenta = new ApplicationUser { LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15) };

        Admite(cuenta, Emision).Should().BeTrue();
    }
}
