using System.Reflection;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

public class TrazaPropiaDePurgasTests
{
    [Fact]
    public void La_tabla_de_trazas_tiene_rls_forzada_y_no_concede_acceso_a_los_roles_de_aplicacion()
    {
        var constructor = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(TrazaPropiaDePurgas).GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new TrazaPropiaDePurgas(), [constructor]);
        var sql = string.Join('\n', constructor.Operations.OfType<SqlOperation>().Select(o => o.Sql));
        sql.Should().Contain("ALTER TABLE public.\"TrazasPurga\" ENABLE ROW LEVEL SECURITY");
        sql.Should().Contain("ALTER TABLE public.\"TrazasPurga\" FORCE ROW LEVEL SECURITY");
        sql.Should().Contain("REVOKE ALL ON TABLE public.\"TrazasPurga\" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento");
        sql.Should().NotContain("GRANT", "los roles de aplicación no acceden a la evidencia transversal");
        sql.Split("INSERT INTO public.\"TrazasPurga\"").Should().HaveCount(4, "cada una de las tres purgas deja su propia traza");
        sql.Should().Contain("pg_catalog.has_table_privilege(p.proowner, 'public.\"TrazasPurga\"', 'INSERT')",
            "un propietario de función que no pueda escribir la traza debe parar la migración, no la purga en producción");
    }
}
