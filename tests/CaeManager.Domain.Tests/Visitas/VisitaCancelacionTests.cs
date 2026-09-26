using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Visitas;

/// <summary>
/// FS-11 (auditoría UX de flujos sin salida, 2026-09-24): cancelar una Visita es
/// un estado reversible, no un borrado lógico.
/// </summary>
public class VisitaCancelacionTests
{
    private static readonly DateOnly Hoy = new(2026, 9, 26);
    private static readonly DateTime Ahora = new(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc);

    private static Visita VisitaActiva() => new(Guid.NewGuid(), Hoy.AddDays(1), Hoy.AddDays(3), "Revisión anual");

    [Fact]
    public void Cancelar_la_saca_de_las_activas_sin_borrarla_ni_tocar_sus_datos()
    {
        var visita = VisitaActiva();
        visita.EstaActiva(Hoy).Should().BeTrue("control positivo: antes de cancelar estaba activa");

        visita.Cancelar(Ahora, "  El cliente aplaza la obra  ");

        visita.EstaCancelada.Should().BeTrue();
        visita.EstaActiva(Hoy).Should().BeFalse("una Visita cancelada no está activa aunque su FechaFin no haya pasado");
        visita.EstaEliminado.Should().BeFalse("cancelar ya no es un borrado lógico");
        visita.CanceladaEnUtc.Should().Be(Ahora);
        visita.MotivoCancelacion.Should().Be("El cliente aplaza la obra");
        visita.FechaInicio.Should().Be(Hoy.AddDays(1));
        visita.Notas.Should().Be("Revisión anual");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void El_motivo_es_opcional(string? motivo)
    {
        var visita = VisitaActiva();

        visita.Cancelar(Ahora, motivo);

        visita.EstaCancelada.Should().BeTrue();
        visita.MotivoCancelacion.Should().BeNull();
    }

    [Fact]
    public void Cancelar_dos_veces_lanza_y_no_pisa_la_primera()
    {
        var visita = VisitaActiva();
        visita.Cancelar(Ahora, "primero");

        var accion = () => visita.Cancelar(Ahora.AddHours(1), "segundo");

        accion.Should().Throw<InvalidOperationException>();
        visita.MotivoCancelacion.Should().Be("primero");
        visita.CanceladaEnUtc.Should().Be(Ahora);
    }

    [Fact]
    public void Reactivar_devuelve_la_visita_al_estado_previo_y_guarda_fecha_y_motivo()
    {
        var visita = VisitaActiva();
        visita.Cancelar(Ahora, "aplazada");

        visita.Reactivar(Ahora.AddHours(2), "Se retoma la obra");

        visita.EstaCancelada.Should().BeFalse();
        visita.EstaActiva(Hoy).Should().BeTrue("vuelve al estado previo: activa, porque sus fechas no han pasado");
        visita.CanceladaEnUtc.Should().BeNull();
        visita.MotivoCancelacion.Should().BeNull();
        visita.ReactivadaEnUtc.Should().Be(Ahora.AddHours(2));
        visita.MotivoReactivacion.Should().Be("Se retoma la obra");
    }

    [Fact]
    public void Reactivar_una_visita_cuyas_fechas_ya_pasaron_la_deja_finalizada_no_activa()
    {
        var visita = new Visita(Guid.NewGuid(), Hoy.AddDays(-5), Hoy.AddDays(-2), null);
        visita.Cancelar(Ahora, null);

        visita.Reactivar(Ahora, null);

        visita.EstaCancelada.Should().BeFalse();
        visita.EstaActiva(Hoy).Should().BeFalse("el estado previo era finalizada, no activa");
    }

    [Fact]
    public void Reactivar_una_visita_no_cancelada_lanza()
    {
        var visita = VisitaActiva();

        var accion = () => visita.Reactivar(Ahora, null);

        accion.Should().Throw<InvalidOperationException>();
        visita.ReactivadaEnUtc.Should().BeNull();
    }

    [Fact]
    public void Volver_a_cancelar_tras_reactivar_limpia_la_reactivacion_anterior()
    {
        var visita = VisitaActiva();
        visita.Cancelar(Ahora, "uno");
        visita.Reactivar(Ahora.AddHours(1), "dos");

        visita.Cancelar(Ahora.AddHours(2), "tres");

        visita.MotivoCancelacion.Should().Be("tres");
        visita.ReactivadaEnUtc.Should().BeNull();
        visita.MotivoReactivacion.Should().BeNull();
    }

    [Fact]
    public void Un_motivo_demasiado_largo_se_rechaza_sin_cambiar_el_estado()
    {
        var visita = VisitaActiva();
        var largo = new string('x', Visita.LongitudMaximaMotivo + 1);

        var accion = () => visita.Cancelar(Ahora, largo);

        accion.Should().Throw<ArgumentException>();
        visita.EstaCancelada.Should().BeFalse();
    }
}
