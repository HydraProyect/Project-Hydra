using CaeManager.Domain.Asignaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Asignaciones;

/// <summary>
/// El cierre derivado de que desaparezca el centro o el trabajador.
/// Ver <see cref="Asignacion.CerrarPorAmbitoEliminado"/>: a diferencia de
/// <c>DarDeBaja</c>, no puede fallar por fecha, porque no hay nadie a quien
/// devolverle el error y una excepción dejaría la asignación viva colgando
/// de un extremo muerto.
/// </summary>
public class AsignacionCierrePorAmbitoEliminadoTests
{
    private static Asignacion CrearAsignacion(DateOnly fechaAlta) =>
        new(Guid.NewGuid(), Guid.NewGuid(), fechaAlta);

    [Fact]
    public void Cierra_la_asignacion_en_la_fecha_indicada()
    {
        var asignacion = CrearAsignacion(new DateOnly(2026, 1, 10));

        asignacion.CerrarPorAmbitoEliminado(new DateOnly(2026, 8, 30));

        asignacion.FechaBaja.Should().Be(new DateOnly(2026, 8, 30));
        asignacion.EstaActiva.Should().BeFalse();
    }

    [Fact]
    public void Ancla_la_baja_al_alta_cuando_el_alta_es_futura()
    {
        // Un alta futura con el centro ya borrado: cerrar en "hoy" sería una
        // baja anterior al alta, que DarDeBaja rechaza. Aquí se ancla al alta
        // en vez de lanzar — la asignación queda cerrada el día que se abre.
        var asignacion = CrearAsignacion(new DateOnly(2026, 12, 1));

        asignacion.CerrarPorAmbitoEliminado(new DateOnly(2026, 8, 30));

        asignacion.FechaBaja.Should().Be(new DateOnly(2026, 12, 1));
        asignacion.EstaActiva.Should().BeFalse();
    }

    [Fact]
    public void No_lanza_cuando_la_fecha_es_anterior_al_alta()
    {
        var asignacion = CrearAsignacion(new DateOnly(2026, 12, 1));

        var cerrar = () => asignacion.CerrarPorAmbitoEliminado(new DateOnly(2020, 1, 1));

        cerrar.Should().NotThrow();
    }

    [Fact]
    public void No_reescribe_una_baja_que_ya_existia()
    {
        // Borrar el trabajador de una asignación ya dada de baja no debe
        // mover su fecha: el historial de dónde ha trabajado cada persona es
        // justo lo que Asignacion existe para conservar.
        var asignacion = CrearAsignacion(new DateOnly(2026, 1, 10));
        asignacion.DarDeBaja(new DateOnly(2026, 3, 15));

        asignacion.CerrarPorAmbitoEliminado(new DateOnly(2026, 8, 30));

        asignacion.FechaBaja.Should().Be(new DateOnly(2026, 3, 15));
    }
}
