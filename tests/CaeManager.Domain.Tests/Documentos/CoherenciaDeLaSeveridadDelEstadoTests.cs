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
        EstadoDocumento.Faltante,
        EstadoDocumento.Vencido,
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
            "lo que falta, antes que lo vencido, lo urgente, lo próximo, lo sin confirmar (detrás de lo malo conocido y " +
            "delante de lo vigente), lo vigente y, al final, lo que no caduca");
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
