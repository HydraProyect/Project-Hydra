using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Paso «Empresa» del alta autónoma y pre-secuestro de la verificación (A4), fijados sobre el texto de la migración. Lo
/// que se fija aquí es la forma: funciones definidoras acotadas, candado antes del reloj, nada del Tenant en lo que
/// devuelven. El comportamiento bajo <c>cae_app_runtime</c> lo mide la suite de integración (solo en CI).
/// </summary>
public class AltaDatosOrganizacionYCredencialContinuacionTests
{
    private const string FirmaConsultar = "app_alta_consultar_enlace(text)";
    private const string FirmaVerificar = "app_alta_verificar_correo(text, text, text)";
    private const string FirmaCompletar = "app_alta_completar_datos_organizacion(text, text, text, text, text, text, boolean, boolean)";

    private static string Up => new AltaDatosOrganizacionYCredencialContinuacion()
        .UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Down => new AltaDatosOrganizacionYCredencialContinuacion()
        .DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Cuerpo(string nombre)
    {
        var inicio = Up.IndexOf($"CREATE FUNCTION public.{nombre}(", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1, nombre);
        return Up[inicio..Up.IndexOf("$$;", inicio, StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData("app_alta_consultar_enlace")]
    [InlineData("app_alta_verificar_correo")]
    [InlineData("app_alta_completar_datos_organizacion")]
    public void Cada_funcion_es_definidora_con_search_path_fijo_y_exige_el_contexto_firmado(string nombre)
    {
        Cuerpo(nombre).Should().Contain("SECURITY DEFINER")
            .And.Contain("SET search_path TO 'pg_catalog', 'pg_temp'")
            .And.Contain("PERFORM public.app_exigir_contexto_firmado();")
            .And.NotContain("DELETE ")
            .And.NotContain("INSERT ");
    }

    [Fact]
    public void Solo_las_ejecuta_el_runtime_la_tabla_no_se_abre_y_Down_deja_la_verificacion_de_un_argumento()
    {
        foreach (var firma in new[] { FirmaConsultar, FirmaVerificar, FirmaCompletar })
        {
            Up.Should().Contain($"REVOKE ALL ON FUNCTION public.{firma}")
                .And.Contain($"GRANT EXECUTE ON FUNCTION public.{firma}");
            Down.Should().Contain($"DROP FUNCTION public.{firma};");
        }

        Up.TrimStart().Should().StartWith("DROP FUNCTION public.app_alta_verificar_correo(text);");
        Up.Should().NotContain("CREATE POLICY", "las tablas siguen sin políticas")
            .And.NotContain("ON TABLE", "las tablas siguen sin privilegios para los roles de la aplicación")
            .And.NotContain("app_alta_caducar", "la caducidad es de A8")
            .And.NotContain("app_alta_borrar", "la limpieza es de A8");
        Down.Should().Contain("CREATE FUNCTION public.app_alta_verificar_correo(p_hash_token text) RETURNS text")
            .And.Contain("GRANT EXECUTE ON FUNCTION public.app_alta_verificar_correo(text) TO cae_app_runtime;");
    }

    [Fact]
    public void Consultar_el_enlace_solo_devuelve_el_hash_de_la_contrasena_con_el_enlace_vigente_en_Iniciada()
    {
        var cuerpo = Cuerpo("app_alta_consultar_enlace");
        cuerpo.Should().Contain("RETURNS TABLE(resultado text, hash_contrasena text)")
            .And.Contain("v_estado <> 'Iniciada'")
            .And.NotContain("UPDATE ", "consultar no escribe");

        // El hash solo sale en la rama «Vigente»; las demás devuelven NULL.
        cuerpo.Should().Contain("RETURN QUERY SELECT 'Vigente'::text, v_hash;");
        cuerpo.Split("RETURN QUERY SELECT").Count(r => r.Contains("v_hash;", StringComparison.Ordinal)).Should().Be(1);
    }

    [Theory]
    [InlineData("app_alta_verificar_correo")]
    [InlineData("app_alta_completar_datos_organizacion")]
    public void Las_transiciones_leen_el_reloj_una_sola_vez_y_despues_del_candado(string nombre)
    {
        var cuerpo = Cuerpo(nombre);
        var bloqueo = cuerpo.IndexOf("FOR UPDATE;", StringComparison.Ordinal);
        bloqueo.Should().BeGreaterThan(-1);
        var reloj = cuerpo.IndexOf("v_ahora := pg_catalog.clock_timestamp()", StringComparison.Ordinal);
        reloj.Should().BeGreaterThan(bloqueo, "el reloj se lee después de FOR UPDATE");
        cuerpo.IndexOf("clock_timestamp", StringComparison.Ordinal).Should().Be(
            cuerpo.LastIndexOf("clock_timestamp", StringComparison.Ordinal), "no hay otra lectura del reloj");
        cuerpo.IndexOf("v_ahora >=", StringComparison.Ordinal).Should().BeGreaterThan(reloj);
        cuerpo.Should().NotContain("now()", "la hora es la del reloj leído con el candado tomado");
    }

    [Fact]
    public void Verificar_exige_bajo_candado_el_mismo_hash_de_contrasena_y_guarda_solo_el_hash_del_credencial()
    {
        var cuerpo = Cuerpo("app_alta_verificar_correo");
        cuerpo.Should().Contain("v_hash IS DISTINCT FROM p_hash_contrasena")
            .And.Contain("\"HashTokenVerificacion\" = NULL", "el enlace se gasta al verificar")
            .And.Contain("\"HashCredencialContinuacion\" = p_hash_credencial")
            .And.Contain("LEAST(v_ahora + interval '2 hours', v_caduca)")
            .And.Contain("p_hash_credencial !~ '^[0-9a-f]{64}$'", "el credencial llega como SHA-256, nunca en claro");
    }

    [Fact]
    public void Completar_no_devuelve_nada_del_Tenant_y_solo_mira_el_CIF_propietario()
    {
        var cuerpo = Cuerpo("app_alta_completar_datos_organizacion");
        cuerpo.Should().Contain("RETURNS TABLE(resultado text, solicitud_alta_id uuid)")
            .And.Contain("SELECT 1 FROM public.\"CifsPropietarioTenant\" c WHERE c.\"Cif\" = p_cif_nif")
            .And.Contain("v_estado <> 'CorreoVerificado'")
            .And.Contain("\"Estado\" = 'DerivadaAAcceso'")
            .And.Contain("\"Estado\" = 'DatosCompletos'")
            .And.NotContain("\"TenantId\"", "ni el Id del Tenant propietario sale ni se lee")
            .And.NotContain("\"Tenants\"");

        // Lo único que sale son el resultado y el Id de la propia Solicitud de alta.
        foreach (var salida in cuerpo.Split("RETURN QUERY SELECT").Skip(1))
            salida[..salida.IndexOf(';')].Trim().Should().MatchRegex(@"^'(NoValido|DatosCompletos|DerivadaAAcceso)'::text, (NULL::uuid|v_id)$");

        // Derivada es final: el credencial deja de servir en la misma sentencia.
        var derivada = cuerpo[cuerpo.IndexOf("\"Estado\" = 'DerivadaAAcceso'", StringComparison.Ordinal)..];
        derivada[..derivada.IndexOf("WHERE", StringComparison.Ordinal)].Should().Contain("\"HashCredencialContinuacion\" = NULL");
    }
}
