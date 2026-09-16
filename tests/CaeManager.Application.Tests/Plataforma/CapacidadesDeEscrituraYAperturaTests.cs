using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// PD-A3, commit 3: exhaustivo sobre TODOS los valores del enum, no solo los
/// que hoy son <c>true</c>. Un <see cref="Theory"/> con <c>InlineData</c>
/// parcial dejaría pasar en verde una capacidad nueva que nadie decidió meter
/// —o no— en estas dos listas explícitas; <see cref="Enum.GetValues{T}"/>
/// obliga a que este test se ponga en rojo el día que el enum crezca, hasta
/// que alguien decida dónde cae la capacidad nueva.
/// </summary>
public class CapacidadesDeEscrituraYAperturaTests
{
    public static IEnumerable<object[]> TodasLasCapacidades() =>
        Enum.GetValues<CapacidadPrivilegio>().Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(TodasLasCapacidades))]
    public void CapacidadesQuePuedenAbrirSesion_admite_exactamente_SoporteLectura_y_Aprovisionamiento(
        CapacidadPrivilegio capacidad)
    {
        var esperado = capacidad is CapacidadPrivilegio.SoporteLectura or CapacidadPrivilegio.Aprovisionamiento;

        CapacidadesQuePuedenAbrirSesion.Admite(capacidad).Should().Be(esperado);
    }

    [Theory]
    [MemberData(nameof(TodasLasCapacidades))]
    public void CapacidadesConCaminoDeEscritura_admite_exactamente_Aprovisionamiento(
        CapacidadPrivilegio capacidad)
    {
        var esperado = capacidad is CapacidadPrivilegio.Aprovisionamiento;

        CapacidadesConCaminoDeEscritura.Admite(capacidad).Should().Be(esperado,
            "BreakGlass permite escribir en el modelo pero no tiene camino todavía — ver SesionPrivilegiadaActivaTests");
    }
}
