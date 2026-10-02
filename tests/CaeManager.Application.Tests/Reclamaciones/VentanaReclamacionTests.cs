using CaeManager.Application.Reclamaciones;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// S4: la ventana de reclamación tiene dos formas (en memoria, <see cref="VentanaReclamacion.EsReclamable"/>, y
/// sobre una consulta, <see cref="VentanaReclamacion.Reclamables"/>, que EF traduce a SQL). Son la misma regla:
/// esta tabla las pasa por las mismas fechas y exige el mismo veredicto, que además fija el contrato (con fecha,
/// como mucho a 3 meses, sin límite inferior, el corte inclusivo).
/// </summary>
public class VentanaReclamacionTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 2);

    public static IEnumerable<object?[]> Fechas() =>
    [
        [(DateOnly?)null, false, "sin fecha de vencimiento: no hay nada que renovar"],
        [(DateOnly?)Hoy.AddYears(-3), true, "vencido desde hace años: sin límite inferior"],
        [(DateOnly?)Hoy.AddDays(-1), true, "vencido ayer"],
        [(DateOnly?)Hoy, true, "vence hoy"],
        [(DateOnly?)new DateOnly(2027, 1, 1), true, "un día antes del límite"],
        [(DateOnly?)new DateOnly(2027, 1, 2), true, "justo en el límite: hoy más 3 meses, inclusivo"],
        [(DateOnly?)new DateOnly(2027, 1, 3), false, "un día después del límite"],
        [(DateOnly?)Hoy.AddYears(1), false, "dentro de un año"],
    ];

    [Theory]
    [MemberData(nameof(Fechas))]
    public void La_forma_en_memoria_y_la_de_consulta_dan_el_mismo_veredicto(DateOnly? fecha, bool esperado, string porque)
    {
        VentanaReclamacion.EsReclamable(fecha, Hoy).Should().Be(esperado, porque);

        var documento = fecha is { } f
            ? Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), Hoy.AddYears(-5), VigenciaDocumento.VenceEl(f))
            : Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), Hoy.AddYears(-5), VigenciaDocumento.SinConfirmar);

        new[] { documento }.AsQueryable().Reclamables(Hoy).Any().Should().Be(esperado, porque);
    }

    [Fact]
    public void El_limite_es_hoy_mas_los_meses_de_la_constante()
    {
        VentanaReclamacion.Meses.Should().Be(3);
        VentanaReclamacion.Limite(Hoy).Should().Be(new DateOnly(2027, 1, 2));
        VentanaReclamacion.Limite(new DateOnly(2026, 11, 30)).Should().Be(new DateOnly(2027, 2, 28),
            "AddMonths recorta al último día del mes, no se desborda");
    }
}
