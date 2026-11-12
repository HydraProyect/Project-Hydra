using CaeManager.Web.Features.Contacto;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// La ventana deslizante de una hora del cupo global de correos del formulario
/// de demo, con un reloj que el test mueve a mano.
/// </summary>
public class CupoGlobalSolicitudDemoTests
{
    private static readonly DateTimeOffset Inicio = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rechaza_al_llegar_al_limite_y_dice_cuando_se_libera_la_primera_plaza()
    {
        var reloj = new RelojManual(Inicio);
        var cupo = new CupoGlobalSolicitudDemo(2, reloj);

        cupo.IntentarReservar(out _, out _, out _).Should().BeTrue();
        reloj.Avanzar(TimeSpan.FromMinutes(10));
        cupo.IntentarReservar(out _, out _, out _).Should().BeTrue();
        reloj.Avanzar(TimeSpan.FromMinutes(5));

        cupo.IntentarReservar(out _, out var reintentarEn, out _).Should().BeFalse();
        reintentarEn.Should().Be(TimeSpan.FromMinutes(45), "la primera plaza se reservó hace 15 minutos");
    }

    [Fact]
    public void Una_plaza_vuelve_justo_al_cumplir_la_hora_y_no_un_segundo_antes()
    {
        var reloj = new RelojManual(Inicio);
        var cupo = new CupoGlobalSolicitudDemo(1, reloj);
        cupo.IntentarReservar(out _, out _, out _).Should().BeTrue();

        reloj.Avanzar(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        cupo.IntentarReservar(out _, out var reintentarEn, out _).Should().BeFalse();
        reintentarEn.Should().Be(TimeSpan.FromSeconds(1));

        reloj.Avanzar(TimeSpan.FromSeconds(1));
        cupo.IntentarReservar(out _, out _, out _).Should().BeTrue(
            "si la ventana no corriera, el formulario quedaría cerrado hasta reiniciar el proceso");
    }

    [Fact]
    public void Liberar_devuelve_la_plaza_de_un_envio_que_no_salio()
    {
        var cupo = new CupoGlobalSolicitudDemo(1, new RelojManual(Inicio));
        cupo.IntentarReservar(out var reserva, out _, out _).Should().BeTrue();

        cupo.Liberar(reserva);

        cupo.IntentarReservar(out _, out _, out _).Should().BeTrue();
    }

    [Fact]
    public void Solo_el_primer_rechazo_de_cada_episodio_pide_aviso()
    {
        var reloj = new RelojManual(Inicio);
        var cupo = new CupoGlobalSolicitudDemo(1, reloj);
        cupo.IntentarReservar(out _, out _, out _).Should().BeTrue();

        cupo.IntentarReservar(out _, out _, out var primero).Should().BeFalse();
        cupo.IntentarReservar(out _, out _, out var segundo).Should().BeFalse();
        primero.Should().BeTrue();
        segundo.Should().BeFalse("un error por rechazo convertiría el aviso en ruido");

        reloj.Avanzar(TimeSpan.FromHours(1));
        cupo.IntentarReservar(out _, out _, out _).Should().BeTrue();
        cupo.IntentarReservar(out _, out _, out var nuevoEpisodio).Should().BeFalse();
        nuevoEpisodio.Should().BeTrue();
    }

    private sealed class RelojManual(DateTimeOffset ahora) : TimeProvider
    {
        private DateTimeOffset ahora = ahora;

        public override DateTimeOffset GetUtcNow() => ahora;

        public void Avanzar(TimeSpan cuanto) => ahora += cuanto;
    }
}
