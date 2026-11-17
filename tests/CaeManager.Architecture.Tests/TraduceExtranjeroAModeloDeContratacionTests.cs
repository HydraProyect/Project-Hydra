using System.Text.RegularExpressions;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>TraduceExtranjeroAModeloDeContratacion</c> sin base de datos: el CHECK de la máscara se rehace alrededor de la traducción,
/// la traducción solo toca requisitos de Trabajador y ninguna función cambia (la lectura inversa X-2 sigue igual). La
/// traducción fila a fila la mide <c>TraduceExtranjeroAModeloDeContratacionTests</c> de la suite de integración (solo en CI).
/// </summary>
public class TraduceExtranjeroAModeloDeContratacionTests
{
    private static readonly TraduceExtranjeroAModeloDeContratacion Migracion = new();

    [Fact]
    public void Ninguna_operacion_toca_funciones_ni_privilegios()
    {
        foreach (var sql in Migracion.UpOperations.Concat(Migracion.DownOperations).OfType<SqlOperation>().Select(o => o.Sql))
            sql.Should().NotContainAny("FUNCTION", "GRANT", "REVOKE", "POLICY");
    }

    [Fact]
    public void La_traduccion_de_requisitos_solo_toca_los_de_Trabajador()
    {
        var updates = Regex.Matches(TraduceExtranjeroAModeloDeContratacion.TraduceMascaras,
            @"UPDATE public\.""RequisitosPlantilla""(?<cuerpo>[^;]*);").Select(m => m.Groups["cuerpo"].Value).ToList();

        updates.Should().HaveCount(3).And.OnlyContain(u => u.Contains("\"Entidad\" = 'Trabajador'"));
    }

    [Fact]
    public void El_Up_quita_el_CHECK_traduce_y_lo_rehace_para_el_bit_Extranjero()
    {
        var operaciones = Migracion.UpOperations;

        operaciones.Should().HaveCount(4);
        operaciones[0].Should().BeOfType<SqlOperation>().Which.Sql.Should().Contain("rolbypassrls");
        operaciones[1].Should().BeOfType<DropCheckConstraintOperation>().Which.Name.Should().Be("CK_RequisitosPlantilla_TiposContratacion");
        operaciones[2].Should().BeOfType<SqlOperation>().Which.Sql.Should().Be(TraduceExtranjeroAModeloDeContratacion.TraduceMascaras);
        operaciones[3].Should().BeOfType<AddCheckConstraintOperation>().Which.Sql
            .Should().Be("\"TiposContratacion\" > 0 AND (\"TiposContratacion\" & ~15) = 0");
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
    public void El_Down_revierte_las_mascaras_y_devuelve_el_CHECK_anterior()
    {
        var operaciones = Migracion.DownOperations;

        operaciones.OfType<SqlOperation>().Should().Contain(o => o.Sql == TraduceExtranjeroAModeloDeContratacion.RevierteMascaras);
        operaciones.OfType<AddCheckConstraintOperation>().Should().ContainSingle().Which.Sql
            .Should().Be("\"TiposContratacion\" > 0 AND (\"TiposContratacion\" & ~7) = 0");
    }
}
