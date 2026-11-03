using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La suite de integración mide el comportamiento en CI; esto fija, sin base de datos, el texto de la migración que
/// una revisión podría romper sin ponerla en rojo: toda resolución transversal pasa por la guardia, y la excepción de
/// la guardia mira la identidad de sesión, no el propietario de la función.
/// </summary>
public class OraculosExigenContextoFirmadoTests
{
    private static string Up => new OraculosTransversalesExigenContextoFirmado()
        .UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Down => new OraculosTransversalesExigenContextoFirmado()
        .DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    [Theory]
    [InlineData("app_cuenta_por_nombre_normalizado(nombre_normalizado text)")]
    [InlineData("app_cuentas_por_email_normalizado(email_normalizado text)")]
    [InlineData("app_tenant_de_cuenta(cuenta_id uuid)")]
    [InlineData("app_tenant_de_webhook_graph(p_conexion uuid)")]
    [InlineData("app_tenant_de_webhook_whatsapp(p_phone_number_id text)")]
    [InlineData("app_actor_plataforma_que_exporta(p_email_normalizado text, p_tenant uuid)")]
    [InlineData("app_nombre_en_uso_por_otra_cuenta(p_nombre_normalizado text, p_cuenta uuid)")]
    [InlineData("app_correo_en_uso_por_otra_cuenta(p_email_normalizado text, p_cuenta uuid)")]
    public void Toda_resolucion_transversal_exige_el_contexto_firmado(string firma)
    {
        var inicio = Up.IndexOf($"FUNCTION public.{firma}", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1, firma);

        var cuerpo = Up[inicio..Up.IndexOf("$$;", inicio, StringComparison.Ordinal)];
        cuerpo.Should().Contain("SECURITY DEFINER")
            .And.Contain("SET search_path TO 'pg_catalog', 'pg_temp'")
            .And.Contain("PERFORM public.app_exigir_contexto_firmado();");
    }

    /// <summary>Las resoluciones transversales que nacen después de esa migración llevan la guardia desde el principio.</summary>
    [Fact]
    public void La_resolucion_de_la_clave_de_integracion_exige_el_contexto_firmado()
    {
        var sql = string.Concat(new AnadeClavesIntegracionTenant().UpOperations.OfType<SqlOperation>().Select(o => o.Sql));
        var inicio = sql.IndexOf("FUNCTION public.app_tenant_de_clave_integracion(hash_clave text)", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1);

        sql[inicio..sql.IndexOf("$$;", inicio, StringComparison.Ordinal)]
            .Should().Contain("SECURITY DEFINER")
            .And.Contain("SET search_path TO 'pg_catalog', 'pg_temp'")
            .And.Contain("PERFORM public.app_exigir_contexto_firmado();");
    }

    /// <summary>Las que se quedaron fuera de la primera migración: mismo contrato, y el Down deja el cuerpo anterior.</summary>
    [Theory]
    [InlineData("app_estado_cuenta_emisora(cuenta_id uuid)")]
    [InlineData("app_tenant_de_clave_api(hash_clave text)")]
    [InlineData("app_sesion_vigente(p_sid uuid, p_usuario uuid)")]
    public void Las_resoluciones_restantes_exigen_el_contexto_firmado(string firma)
    {
        var migracion = new ResolucionesRestantesExigenContextoFirmado();
        var up = migracion.UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        var down = migracion.DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

        var inicio = up.IndexOf($"CREATE OR REPLACE FUNCTION public.{firma}", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1, firma);
        up[inicio..up.IndexOf("$$;", inicio, StringComparison.Ordinal)]
            .Should().Contain("SECURITY DEFINER")
            .And.Contain("SET search_path TO 'pg_catalog', 'pg_temp'")
            .And.Contain("PERFORM public.app_exigir_contexto_firmado();");

        down.Should().Contain($"CREATE OR REPLACE FUNCTION public.{firma}")
            .And.NotContain("app_exigir_contexto_firmado");
        (up + down).Should().NotContain("GRANT ", "CREATE OR REPLACE conserva las ACL, también el EXECUTE de cae_app_soporte")
            .And.NotContain("REVOKE ");
    }

    [Fact]
    public void La_guardia_solo_exime_a_la_sesion_que_ya_evita_RLS()
    {
        var inicio = Up.IndexOf("FUNCTION public.app_exigir_contexto_firmado()", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1);
        var guardia = Up[inicio..Up.IndexOf("$$;", inicio, StringComparison.Ordinal)];

        guardia.Should().Contain("rolname = session_user AND (rolsuper OR rolbypassrls)")
            .And.Contain("COALESCE(public.app_ctx_valido(), false)")
            .And.Contain("USING ERRCODE = '42501'")
            .And.NotContain("SECURITY DEFINER");
        guardia.Should().NotContain("current_user", "dentro de una definidora current_user es el propietario: eximiría a todos");
    }

    [Fact]
    public void Las_dos_de_unicidad_solo_las_ejecuta_el_runtime_y_el_Down_deja_el_esquema_anterior()
    {
        foreach (var firma in new[]
        {
            "app_nombre_en_uso_por_otra_cuenta(text, uuid)",
            "app_correo_en_uso_por_otra_cuenta(text, uuid)",
        })
        {
            Up.Should().Contain($"REVOKE ALL ON FUNCTION public.{firma} FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;")
                .And.Contain($"GRANT EXECUTE ON FUNCTION public.{firma} TO cae_app_runtime;");
            Down.Should().Contain($"DROP FUNCTION public.{firma};");
        }

        Up.Should().NotContain("cae_app_runtime, cae_app_soporte", "ninguna definidora se abre a otro rol");
        Down.Should().Contain("DROP FUNCTION public.app_exigir_contexto_firmado();")
            .And.NotContain("PERFORM public.app_exigir_contexto_firmado();");
    }
}
