using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>app_revocar_sesiones_de_cuenta</c> escribe por encima de <c>solo_la_propia_cuenta_revoca</c>: esto
/// fija, sin base de datos, el texto que una revisión podría romper sin ponerla en rojo. El
/// comportamiento bajo runtime lo prueba <c>RegistroSesionesUsuarioBajoRuntimeTests</c> en CI.
/// </summary>
public class RevocarSesionesDeCuentaMigracionTests
{
    private static string Up => new RevocarSesionesDeCuenta()
        .UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Down => new RevocarSesionesDeCuenta()
        .DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    [Fact]
    public void Exige_contexto_firmado_y_el_Tenant_de_la_cuenta_antes_de_escribir()
    {
        var sql = Up;
        var escritura = sql.IndexOf("UPDATE public.\"SesionesUsuario\"", StringComparison.Ordinal);

        sql.IndexOf("PERFORM public.app_exigir_contexto_firmado();", StringComparison.Ordinal)
            .Should().BeInRange(0, escritura);
        sql.IndexOf("IS DISTINCT FROM public.app_ctx_tenant_id() THEN", StringComparison.Ordinal)
            .Should().BeInRange(0, escritura);
        sql.Should().Contain("SECURITY DEFINER")
            .And.Contain("SET search_path TO 'pg_catalog', 'pg_temp'")
            .And.Contain("USING ERRCODE = '42501'");
    }

    [Fact]
    public void Solo_revoca_sesiones_vivas_de_esa_cuenta_y_un_nulo_no_las_salva_a_todas()
    {
        var sql = Up;

        sql.Should().Contain("WHERE \"UsuarioId\" = p_usuario")
            .And.Contain("AND \"RevocadaEnUtc\" IS NULL")
            .And.Contain("AND \"Sid\" IS DISTINCT FROM p_conservar", "con «<>», un nulo no revocaría ninguna y no daría error");
    }

    [Fact]
    public void Solo_la_ejecuta_el_runtime_y_el_Down_la_retira()
    {
        Up.Should().Contain("REVOKE ALL ON FUNCTION public.app_revocar_sesiones_de_cuenta(uuid, uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;")
            .And.Contain("GRANT EXECUTE ON FUNCTION public.app_revocar_sesiones_de_cuenta(uuid, uuid) TO cae_app_runtime;");
        Down.Should().Contain("DROP FUNCTION public.app_revocar_sesiones_de_cuenta(uuid, uuid);");
    }
}
