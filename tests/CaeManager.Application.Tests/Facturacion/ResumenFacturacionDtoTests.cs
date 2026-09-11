using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Domain.Facturacion;
using FluentAssertions;

namespace CaeManager.Application.Tests.Facturacion;

/// <summary>
/// <see cref="ResumenFacturacionDto.CalcularTotalesPorMoneda"/>. Defecto
/// detectado el 2026-09-11: el resumen sumaba <c>Subtotal</c> de todas las
/// líneas sin mirar su moneda y rotulaba esa cifra con la moneda de la
/// primera tarifa — con tarifas en EUR y USD a la vez, el total exportado a
/// Excel (150) no correspondía a ninguna de las dos monedas.
/// </summary>
public class ResumenFacturacionDtoTests
{
    private static LineaFacturacionDto Linea(decimal subtotal, string moneda) =>
        new(ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 1, subtotal, moneda, subtotal);

    [Fact]
    public void Sin_lineas_no_hay_totales()
    {
        var totales = ResumenFacturacionDto.CalcularTotalesPorMoneda([]);

        totales.Should().BeEmpty();
    }

    [Fact]
    public void Una_sola_moneda_da_un_solo_total_con_la_suma_de_sus_subtotales()
    {
        var lineas = new[] { Linea(100m, "EUR"), Linea(50m, "EUR") };

        var totales = ResumenFacturacionDto.CalcularTotalesPorMoneda(lineas);

        totales.Should().ContainSingle().Which.Should().Be(new TotalPorMonedaDto("EUR", 150m));
    }

    [Fact]
    public void Dos_monedas_dan_un_total_por_cada_una_sin_sumarlas_entre_si()
    {
        var lineas = new[] { Linea(100m, "EUR"), Linea(50m, "USD") };

        var totales = ResumenFacturacionDto.CalcularTotalesPorMoneda(lineas);

        totales.Should().BeEquivalentTo(
        [
            new TotalPorMonedaDto("EUR", 100m),
            new TotalPorMonedaDto("USD", 50m)
        ], "el total de cada moneda es la suma de SUS líneas, nunca la de las 150 combinadas");
    }

    [Fact]
    public void Varias_lineas_de_la_misma_moneda_intercaladas_con_otra_no_se_mezclan()
    {
        var lineas = new[] { Linea(30m, "EUR"), Linea(50m, "USD"), Linea(20m, "EUR") };

        var totales = ResumenFacturacionDto.CalcularTotalesPorMoneda(lineas);

        totales.Should().BeEquivalentTo(
        [
            new TotalPorMonedaDto("EUR", 50m),
            new TotalPorMonedaDto("USD", 50m)
        ]);
    }

    [Fact]
    public void La_moneda_se_normaliza_para_no_duplicar_el_total_por_mayusculas_o_espacios()
    {
        var lineas = new[] { Linea(10m, "eur"), Linea(5m, " EUR ") };

        var totales = ResumenFacturacionDto.CalcularTotalesPorMoneda(lineas);

        totales.Should().ContainSingle().Which.Should().Be(new TotalPorMonedaDto("EUR", 15m));
    }
}
