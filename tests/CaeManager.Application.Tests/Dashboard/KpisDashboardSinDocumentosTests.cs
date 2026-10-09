using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// Decisión del propietario, 2026-10-09: «que no dé 100 %». Sin ningún Documento de Trabajador en el alcance, la tasa
/// de Inicio no puede ser el 100 de «nada que contar» cuando los Centros de Trabajo sí exigen documentación: enseña lo
/// mismo que Centros y Empresas para esos datos (0 % con pares exigidos; «sin datos» sin ninguno, que es el
/// <c>null</c> de <see cref="FraccionCumplimiento.Porcentaje"/>).
/// </summary>
public class KpisDashboardSinDocumentosTests
{
    [Fact]
    public void Sin_documentos_y_con_pares_exigidos_la_tasa_es_cero_y_hay_datos_que_medir()
    {
        KpisDashboardDto.SinDocumentos(FraccionCumplimiento.SinRequisitos, paresExigidos: 19)
            .Should().Be((0, false), "diecinueve pares exigidos y ninguno cumplido es 0 %, como en Centros y Empresas");
    }

    [Fact]
    public void Sin_documentos_y_sin_pares_exigidos_no_hay_nada_que_medir()
    {
        KpisDashboardDto.SinDocumentos(FraccionCumplimiento.SinRequisitos, paresExigidos: 0)
            .Should().Be((100, true), "sin requisitos no hay porcentaje: el 100 es un valor neutro que SinDatos impide pintar");
    }

    /// <summary>Control: con algún documento la tasa es la de la fracción, exija lo que exija el Centro.</summary>
    [Theory]
    [InlineData(8, 10, 0, 80)]
    [InlineData(8, 10, 19, 80)]
    [InlineData(0, 3, 19, 0)]
    [InlineData(3, 3, 0, 100)]
    public void Con_documentos_la_tasa_sigue_siendo_la_de_la_fraccion(int alDia, int requeridos, int paresExigidos, int tasaEsperada)
    {
        KpisDashboardDto.SinDocumentos(new FraccionCumplimiento(alDia, requeridos), paresExigidos)
            .Should().Be((tasaEsperada, false));
    }
}
