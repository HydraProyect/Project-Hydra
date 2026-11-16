using System.Globalization;
using System.Security.Claims;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Issue #1891 (ASVS 4.0.3 V3.3.2): la vigencia de la sesión de origen de un token de extensión, con las reglas de la
/// cookie (<see cref="CaducidadSesion"/>). Emitir exige que la sesión siga vigente y guarda su inicio y su interacción
/// más reciente; admitir el token exige que siga sin pasar el tope ni el plazo del principal de la cuenta.
/// </summary>
public class SesionOrigenExtensionTests
{
    private static readonly DateTimeOffset Ahora = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private readonly ActividadSesiones _actividad = new();
    private readonly Reloj _reloj = new(Ahora);
    private readonly Guid _sid = Guid.NewGuid();

    private SesionOrigenExtension Servicio() => new(_actividad, _reloj);

    [Fact]
    public void Emitir_guarda_el_inicio_y_la_interaccion_mas_reciente()
    {
        var sesion = Sesion(inicio: Ahora.AddMinutes(-20), interaccion: Ahora.AddMinutes(-10));

        Servicio().MarcasVigentes(sesion).Should().Be(new MarcasSesionOrigen(Ahora.AddMinutes(-20), Ahora.AddMinutes(-10)));

        // El circuito conserva el principal de cuando se conectó: vale la señal anotada después.
        _actividad.Anotar(_sid, Ahora.AddMinutes(-1));
        Servicio().MarcasVigentes(sesion).Should().Be(new MarcasSesionOrigen(Ahora.AddMinutes(-20), Ahora.AddMinutes(-1)));

        // Sin marca de interacción cuenta el inicio, nunca el momento actual.
        Servicio().MarcasVigentes(Sesion(inicio: Ahora.AddMinutes(-3), interaccion: null, otraSid: Guid.NewGuid()))
            .Should().Be(new MarcasSesionOrigen(Ahora.AddMinutes(-3), Ahora.AddMinutes(-3)));
    }

    [Fact]
    public void Una_sesion_ya_vencida_no_emite()
    {
        Servicio().MarcasVigentes(Sesion(inicio: Ahora.AddMinutes(-40), interaccion: Ahora.AddMinutes(-30)))
            .Should().BeNull("30 minutos sin interacción con el plazo por defecto");
        Servicio().MarcasVigentes(Sesion(inicio: Ahora.AddMinutes(-40), interaccion: Ahora.AddMinutes(-6), minutos: 5))
            .Should().BeNull("el plazo de 5 minutos del Tenant propietario que trae la sesión");
        Servicio().MarcasVigentes(Sesion(inicio: Ahora.AddHours(-12), interaccion: Ahora))
            .Should().BeNull("doce horas desde el inicio");
        Servicio().MarcasVigentes(Sesion(inicio: null, interaccion: Ahora)).Should().BeNull("sin hora de inicio");
        Servicio().MarcasVigentes(Sesion(inicio: Ahora.AddMinutes(-1), interaccion: Ahora, conSid: false)).Should().BeNull("sin sesión");
    }

    [Fact]
    public void El_token_vale_mientras_la_sesion_de_origen_tiene_interaccion_dentro_del_plazo_de_la_cuenta()
    {
        var marcas = new MarcasSesionOrigen(Ahora.AddMinutes(-60), Ahora.AddMinutes(-29));

        Servicio().SigueVigente(Cuenta(minutos: 30), _sid, marcas).Should().BeTrue();
        Servicio().SigueVigente(Cuenta(minutos: 15), _sid, marcas).Should().BeFalse("rige el plazo del principal de la cuenta");
        Servicio().SigueVigente(Cuenta(minutos: null), _sid, marcas).Should().BeTrue("sin claim, el plazo por defecto");
        Servicio().SigueVigente(Cuenta(minutos: 9999), _sid, marcas with { UltimaInteraccion = Ahora.AddMinutes(-31) })
            .Should().BeFalse("un plazo fuera de rango vale el de por defecto, nunca uno mayor");

        _reloj.Ahora = Ahora.AddMinutes(1);
        Servicio().SigueVigente(Cuenta(minutos: 30), _sid, marcas).Should().BeFalse("30 minutos sin interacción");
        _actividad.Leer(_sid).Should().BeNull("comprobar el token no anota ninguna interacción");

        _actividad.Anotar(_sid, Ahora);
        Servicio().SigueVigente(Cuenta(minutos: 30), _sid, marcas).Should().BeTrue("la interfaz web interactuó después de emitir");
        Servicio().SigueVigente(Cuenta(minutos: 30), Guid.NewGuid(), marcas).Should().BeFalse("la de otra sesión no cuenta");
    }

    [Fact]
    public void El_token_no_vale_pasado_el_tope_ni_con_marcas_del_futuro()
    {
        Servicio().SigueVigente(Cuenta(minutos: 480), _sid, new MarcasSesionOrigen(Ahora.AddHours(-12), Ahora)).Should().BeFalse();
        Servicio().SigueVigente(Cuenta(minutos: 480), _sid, new MarcasSesionOrigen(Ahora.AddHours(-12).AddSeconds(1), Ahora)).Should().BeTrue();
        Servicio().SigueVigente(Cuenta(minutos: 30), _sid, new MarcasSesionOrigen(Ahora.AddMinutes(1), Ahora.AddMinutes(1))).Should().BeFalse();
        Servicio().SigueVigente(Cuenta(minutos: 30), _sid, new MarcasSesionOrigen(Ahora.AddMinutes(-5), Ahora.AddMinutes(1))).Should().BeFalse();
    }

    private ClaimsPrincipal Sesion(DateTimeOffset? inicio, DateTimeOffset? interaccion, int? minutos = null, Guid? otraSid = null, bool conSid = true)
    {
        var claims = new List<Claim>();
        if (conSid)
            claims.Add(new Claim(SignInManagerCuentaDesactivada.ClaimSid, (otraSid ?? _sid).ToString("N")));
        if (inicio is { } i) claims.Add(new Claim(CaducidadSesion.MarcaInicio, Segundos(i)));
        if (interaccion is { } m) claims.Add(new Claim(CaducidadSesion.MarcaInteraccion, Segundos(m)));
        if (minutos is { } n) claims.Add(new Claim(TenantClaimsPrincipalFactory.TipoClaimMinutosInactividad, n.ToString(CultureInfo.InvariantCulture)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie"));
    }

    private static ClaimsPrincipal Cuenta(int? minutos) =>
        new(new ClaimsIdentity(
            minutos is { } n ? new[] { new Claim(TenantClaimsPrincipalFactory.TipoClaimMinutosInactividad, n.ToString(CultureInfo.InvariantCulture)) } : Array.Empty<Claim>(),
            ExtensionAuthenticationSchemeOptions.NombreEsquema));

    private static string Segundos(DateTimeOffset momento) => momento.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private sealed class Reloj(DateTimeOffset ahora) : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; } = ahora;
        public override DateTimeOffset GetUtcNow() => Ahora;
    }
}
