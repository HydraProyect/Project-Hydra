using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

public class FalloCerradoLecturaInboundTests
{
    [Fact]
    public void La_migracion_protege_las_tres_firmas_y_revierte_la_misma_guardia()
    {
        var migracion = new FalloCerradoLecturaInbound();
        var up = migracion.UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        var down = migracion.DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        foreach (var sql in new[] { up, down })
        {
            sql.Should().Contain("public.app_inbound_autorizacion_del_contrato(uuid)")
                .And.Contain("public.app_inbound_documentos_autorizados(uuid)")
                .And.Contain("public.app_inbound_archivo_autorizado(uuid,uuid)")
                .And.Contain("rolname = current_user AND (rolsuper OR rolbypassrls)")
                .And.Contain("pg_get_functiondef")
                .And.Contain("<> 1");
            sql.Should().NotContain("GRANT ");
            sql.Should().NotContain("\r", "el Down busca la guardia literal, y no puede depender del fin de línea de la compilación");
        }
        up.Should().Contain("IF false THEN");
        down.Should().Contain("IF true THEN");
    }
}
