using CaeManager.Application.Dashboard.Queries;
using CaeManager.Web.Features.CuadroDireccion;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// La suma del ámbito «Equipo de un Gestor CAE» del Cuadro de dirección: volúmenes de las Empresas de su cartera,
/// juntos. El cumplimiento se recalcula sobre los recuentos sumados, no promediando porcentajes.
/// </summary>
public class AgregadosCuadroTests
{
    private static readonly DateTime Ahora = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    private static CifrasCuadroDireccionDto Cifras(int trabajadores, int vigentes, int vencidos, EstadoCifraMensual estado = EstadoCifraMensual.Estimado, int? gestiones = 10)
    {
        CifraMensual c(int v) => new(v, estado);
        return new CifrasCuadroDireccionDto(
            c(trabajadores), c(1), c(0), c(2), c(1), c(5), c(3),
            new SolicitudesDelMesDto(c(4), [new FranjaSolicitudesDto(DayOfWeek.Monday, 9, 2)]),
            new CumplimientoActualDto(vigentes, 0, 0, vencidos, 0, 0, 0, 0, false, Ahora),
            gestiones is { } g ? c(g) : null);
    }

    [Fact]
    public void Sumar_cifras_suma_los_volumenes_y_las_franjas()
    {
        var suma = AgregadosCuadro.SumarCifras([Cifras(100, 9, 1), Cifras(50, 1, 1)], Ahora);

        suma.TrabajadoresActivos.Valor.Should().Be(150);
        suma.Solicitudes.Total.Valor.Should().Be(8);
        suma.Solicitudes.PorFranja.Should().ContainSingle().Which.Cantidad.Should().Be(4);
        suma.Gestiones!.Valor.Should().Be(20);
    }

    [Fact]
    public void El_cumplimiento_se_recalcula_sobre_los_recuentos_y_no_como_promedio_de_porcentajes()
    {
        // 9 de 10 al 90 % y 1 de 2 al 50 %: el promedio de porcentajes daría 70 %; sobre los recuentos, 10 de 12.
        var suma = AgregadosCuadro.SumarCifras([Cifras(1, 9, 1), Cifras(1, 1, 1)], Ahora);

        suma.Cumplimiento.Vigentes.Should().Be(10);
        suma.Cumplimiento.Vencidos.Should().Be(2);
        suma.Cumplimiento.Porcentaje.Should().Be((int)Math.Round(10 * 100m / 12m));
    }

    [Fact]
    public void Una_cifra_sin_gestiones_deja_las_gestiones_del_conjunto_sin_dato()
    {
        var suma = AgregadosCuadro.SumarCifras([Cifras(1, 1, 0), Cifras(1, 1, 0, gestiones: null)], Ahora);

        suma.Gestiones.Should().BeNull();
    }

    [Theory]
    [InlineData(EstadoCifraMensual.Existe, EstadoCifraMensual.Estimado, EstadoCifraMensual.Estimado)]
    [InlineData(EstadoCifraMensual.Existe, EstadoCifraMensual.Parcial, EstadoCifraMensual.Parcial)]
    [InlineData(EstadoCifraMensual.Existe, EstadoCifraMensual.Existe, EstadoCifraMensual.Existe)]
    public void El_estado_comun_es_el_menos_firme(EstadoCifraMensual a, EstadoCifraMensual b, EstadoCifraMensual esperado) =>
        AgregadosCuadro.EstadoComun([a, b]).Should().Be(esperado);

    [Fact]
    public void Sumar_puntos_agrupa_por_mes_y_suma_la_facturacion_por_concepto()
    {
        PuntoSerieCuadroDto Punto(int mes, int trabajadores, decimal importe) => new(
            2026, mes, EstadoCifraMensual.Estimado, Cifras(trabajadores, 1, 0),
            [new LineaFacturacionCuadroDto(CaeManager.Domain.Dashboard.ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, "Visita de Trabajador extranjero", "EUR", trabajadores, importe)]);

        var suma = AgregadosCuadro.SumarPuntos(
            [[Punto(8, 10, 20m), Punto(9, 12, 24m)], [Punto(9, 5, 10m)]], Ahora);

        suma.Select(p => p.Mes).Should().Equal(8, 9);
        suma[1].Cifras.TrabajadoresActivos.Valor.Should().Be(17);
        suma[1].Facturacion!.Single().Importe.Should().Be(34m);
    }

    [Fact]
    public void Sumar_dias_junta_las_gestiones_del_mismo_dia()
    {
        var dia = new DateOnly(2026, 10, 5);

        var suma = AgregadosCuadro.SumarDias([[new GestionesDelDiaDto(dia, 3)], [new GestionesDelDiaDto(dia, 4), new GestionesDelDiaDto(dia.AddDays(1), 1)]]);

        suma.Select(d => d.Cantidad).Should().Equal(7, 1);
    }
}
