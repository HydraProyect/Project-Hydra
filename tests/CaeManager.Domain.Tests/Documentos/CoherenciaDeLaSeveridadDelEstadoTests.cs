using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// S4: el orden de gravedad de un <see cref="EstadoDocumento"/> es una sola regla
/// (<see cref="SeveridadEstadoDocumento"/>). Antes de S4 vivía copiada en seis pantallas y consultas; esta
/// tabla fija el orden que todas repetían y exige que cada valor del enum tenga rango propio (un estado
/// nuevo sin rango cae en el último puesto sin que nadie lo decida).
/// </summary>
public class CoherenciaDeLaSeveridadDelEstadoTests
{
    /// <summary>Lo más grave primero. Es la tabla de entrada: cada fila fija un rango.</summary>
    private static readonly EstadoDocumento[] DeMasAMenosGrave =
    [
        EstadoDocumento.Vencido,
        EstadoDocumento.Faltante,
        EstadoDocumento.EnTolerancia,
        EstadoDocumento.Urgente,
        EstadoDocumento.Proximo,
        EstadoDocumento.SinConfirmar,
        EstadoDocumento.Vigente,
        EstadoDocumento.SinCaducidad,
    ];

    [Fact]
    public void El_orden_de_gravedad_es_el_de_la_tabla()
    {
        var ordenados = Enum.GetValues<EstadoDocumento>().OrderBy(SeveridadEstadoDocumento.Rango).ToArray();

        ordenados.Should().Equal(DeMasAMenosGrave,
            "lo vencido, antes que lo que falta (orden fijado el 2026-10-03), lo vencido en tolerancia (ya venció pero aún vale " +
            "para acceder), lo urgente, lo próximo, lo sin confirmar (detrás de lo malo conocido y delante de lo vigente), " +
            "lo vigente y, al final, lo que no caduca");
    }

    /// <summary>
    /// Revisión 1:1 (2026-10-21): el estado del Trabajador es uno solo en el listado, Empresa 360 y Trabajador 360. Lo
    /// exigido sin documento lo agrava a Pendiente salvo que ya sea más grave (Vencido); sin faltantes no cambia.
    /// </summary>
    [Theory]
    [InlineData(EstadoDocumento.Vencido, true, EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Faltante, true, EstadoDocumento.Faltante)]
    [InlineData(EstadoDocumento.EnTolerancia, true, EstadoDocumento.Faltante)]
    [InlineData(EstadoDocumento.Urgente, true, EstadoDocumento.Faltante)]
    [InlineData(EstadoDocumento.SinConfirmar, true, EstadoDocumento.Faltante)]
    [InlineData(EstadoDocumento.Vigente, true, EstadoDocumento.Faltante)]
    [InlineData(EstadoDocumento.SinCaducidad, true, EstadoDocumento.Faltante)]
    [InlineData(EstadoDocumento.Urgente, false, EstadoDocumento.Urgente)]
    [InlineData(EstadoDocumento.Vigente, false, EstadoDocumento.Vigente)]
    public void Lo_exigido_sin_documento_agrava_el_estado_a_Pendiente_salvo_si_ya_es_vencido(
        EstadoDocumento estado, bool hayFaltantes, EstadoDocumento esperado) =>
        SeveridadEstadoDocumento.ConFaltantes(estado, hayFaltantes).Should().Be(esperado);

    [Fact]
    public void Sin_documentos_registrados_lo_exigido_sin_documento_es_Pendiente_y_sin_exigir_nada_no_hay_estado()
    {
        SeveridadEstadoDocumento.ConFaltantes(null, hayFaltantes: true).Should().Be(EstadoDocumento.Faltante);
        SeveridadEstadoDocumento.ConFaltantes(null, hayFaltantes: false).Should().BeNull();
    }

    [Fact]
    public void Cada_valor_del_enum_tiene_un_rango_propio_y_la_tabla_los_cubre_todos()
    {
        Enum.GetValues<EstadoDocumento>().Should().BeEquivalentTo(DeMasAMenosGrave,
            "un estado nuevo hay que colocarlo en SeveridadEstadoDocumento.Rango Y en esta tabla");

        DeMasAMenosGrave.Select(SeveridadEstadoDocumento.Rango).Should().OnlyHaveUniqueItems(
            "dos estados con el mismo rango empatarían y el orden dependería de otra clave");
    }

    [Fact]
    public void Un_valor_fuera_del_enum_va_el_ultimo()
    {
        SeveridadEstadoDocumento.Rango((EstadoDocumento)999)
            .Should().BeGreaterThan(DeMasAMenosGrave.Max(SeveridadEstadoDocumento.Rango));
    }
}
