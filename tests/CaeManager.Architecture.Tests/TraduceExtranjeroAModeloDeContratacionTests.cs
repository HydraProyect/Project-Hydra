using System.Text.RegularExpressions;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>TraduceExtranjeroAModeloDeContratacion</c> sin base de datos: la lectura inversa X-2 (<c>app_inbound_tec_requerimientos()</c>)
/// se sustituye por la misma función sin <c>"SePideA"</c> y con el mismo contrato de seguridad, el CHECK de la máscara se rehace
/// alrededor de la traducción y el Down devuelve la función original. La traducción fila a fila la mide
/// <c>TraduceExtranjeroAModeloDeContratacionTests</c> de la suite de integración (solo en CI).
/// </summary>
public class TraduceExtranjeroAModeloDeContratacionTests
{
    private static readonly TraduceExtranjeroAModeloDeContratacion Migracion = new();

    private static string Normalizado(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    [Fact]
    public void La_funcion_nueva_es_la_original_sin_SePideA_con_el_mismo_contrato_de_seguridad()
    {
        var original = Normalizado(AnadeLecturaRequerimientosTec.CreaFuncionRequerimientosTec);
        var esperada = "DROP FUNCTION public.app_inbound_tec_requerimientos(); "
            + original.Replace("\"SePideA\" text, ", string.Empty, StringComparison.Ordinal)
                .Replace("r.\"SePideA\"::text, ", string.Empty, StringComparison.Ordinal);

        esperada.Should().NotContain("SePideA");
        Normalizado(TraduceExtranjeroAModeloDeContratacion.CreaFuncionRequerimientosTecSinSePideA).Should().Be(esperada,
            "solo cambia la columna retirada: SECURITY DEFINER, search_path, guardias, filas, REVOKE y GRANT siguen iguales");
        Regex.Matches(TraduceExtranjeroAModeloDeContratacion.CreaFuncionRequerimientosTecSinSePideA, @"GRANT\s")
            .Should().ContainSingle("ningún otro rol recibe nada");
    }

    [Fact]
    public void El_Up_quita_el_CHECK_traduce_lo_rehace_para_el_bit_Extranjero_y_cambia_la_funcion()
    {
        var operaciones = Migracion.UpOperations;

        operaciones.Should().HaveCount(5);
        operaciones[0].Should().BeOfType<SqlOperation>().Which.Sql.Should().Contain("rolbypassrls");
        operaciones[1].Should().BeOfType<DropCheckConstraintOperation>().Which.Name.Should().Be("CK_RequisitosPlantilla_TiposContratacion");
        operaciones[2].Should().BeOfType<SqlOperation>().Which.Sql.Should().Be(TraduceExtranjeroAModeloDeContratacion.TraduceMascaras);
        operaciones[3].Should().BeOfType<AddCheckConstraintOperation>().Which.Sql
            .Should().Be("\"TiposContratacion\" > 0 AND (\"TiposContratacion\" & ~15) = 0");
        operaciones[4].Should().BeOfType<SqlOperation>().Which.Sql
            .Should().Be(TraduceExtranjeroAModeloDeContratacion.CreaFuncionRequerimientosTecSinSePideA);
    }

    [Fact]
    public void La_traduccion_no_reescribe_SePideA_ni_el_modelo_de_contratacion_de_ningun_Trabajador()
    {
        foreach (var sql in new[] { TraduceExtranjeroAModeloDeContratacion.TraduceMascaras, TraduceExtranjeroAModeloDeContratacion.RevierteMascaras })
        {
            Regex.Matches(sql, @"SET\s+""(?<columna>\w+)""").Select(m => m.Groups["columna"].Value).Distinct()
                .Should().BeSubsetOf(["TiposContratacionDeSerie", "TiposContratacion"]);
            sql.Should().NotContain("\"Trabajadores\"").And.NotContain("\"TipoContratacion\"");
        }
    }

    [Fact]
    public void El_Down_devuelve_la_funcion_original_y_el_CHECK_anterior()
    {
        var operaciones = Migracion.DownOperations;

        operaciones.OfType<SqlOperation>().Should().Contain(o =>
            o.Sql == "DROP FUNCTION public.app_inbound_tec_requerimientos();\n" + AnadeLecturaRequerimientosTec.CreaFuncionRequerimientosTec);
        operaciones.OfType<SqlOperation>().Should().Contain(o => o.Sql == TraduceExtranjeroAModeloDeContratacion.RevierteMascaras);
        operaciones.OfType<AddCheckConstraintOperation>().Should().ContainSingle().Which.Sql
            .Should().Be("\"TiposContratacion\" > 0 AND (\"TiposContratacion\" & ~7) = 0");
    }
}
