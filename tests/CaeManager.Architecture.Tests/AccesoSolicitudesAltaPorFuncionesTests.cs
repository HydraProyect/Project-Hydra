using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La vía del registro anónimo del alta autónoma a <c>SolicitudesAlta</c> (A3), fijada sobre el texto de la
/// migración: dos funciones <c>SECURITY DEFINER</c> acotadas en vez de abrir la tabla a <c>cae_app_runtime</c>.
/// El comportamiento bajo el rol real lo mide <c>RegistroAltaBajoRuntimeTests</c> en la suite de integración.
/// </summary>
public class AccesoSolicitudesAltaPorFuncionesTests
{
    private const string FirmaIniciar =
        "app_alta_iniciar_solicitud(uuid, text, text, text, text, timestamp with time zone, text, text, timestamp with time zone, timestamp with time zone)";

    private const string FirmaVerificar = "app_alta_verificar_correo(text)";

    private static string Up => new AccesoSolicitudesAltaPorFunciones()
        .UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Down => new AccesoSolicitudesAltaPorFunciones()
        .DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Cuerpo(string nombre)
    {
        var inicio = Up.IndexOf($"CREATE FUNCTION public.{nombre}(", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1, nombre);
        return Up[inicio..Up.IndexOf("$$;", inicio, StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData("app_alta_iniciar_solicitud")]
    [InlineData("app_alta_verificar_correo")]
    public void Cada_funcion_es_definidora_con_search_path_fijo_y_exige_el_contexto_firmado(string nombre)
    {
        Cuerpo(nombre).Should().Contain("SECURITY DEFINER")
            .And.Contain("SET search_path TO 'pg_catalog', 'pg_temp'")
            .And.Contain("PERFORM public.app_exigir_contexto_firmado();");
    }

    [Fact]
    public void Solo_las_ejecuta_el_runtime_y_la_tabla_no_se_abre()
    {
        foreach (var firma in new[] { FirmaIniciar, FirmaVerificar })
        {
            Up.Should().Contain($"REVOKE ALL ON FUNCTION public.{firma}")
                .And.Contain($"GRANT EXECUTE ON FUNCTION public.{firma}");
            Down.Should().Contain($"DROP FUNCTION public.{firma};");
        }

        Up.Should().Contain("FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;")
            .And.NotContain("CREATE POLICY", "la tabla sigue sin políticas")
            .And.NotContain("ON TABLE", "la tabla sigue sin privilegios para los roles de la aplicación")
            .And.NotContain("ON public.\"SolicitudesAlta\" TO", "ningún GRANT sobre la tabla");
    }

    [Fact]
    public void Iniciar_solo_inserta_en_Iniciada_y_no_devuelve_nada()
    {
        var cuerpo = Cuerpo("app_alta_iniciar_solicitud");
        Up.Should().Contain("timestamp with time zone) RETURNS void");
        cuerpo.Should().Contain("p_id, 'Iniciada',")
            .And.Contain("'^[0-9a-f]{64}$'", "el token llega como SHA-256, nunca en claro")
            .And.Contain("interval '24 hours'")
            .And.Contain("interval '168 hours'")
            .And.NotContain("UPDATE ")
            .And.NotContain("DELETE ");
    }

    [Fact]
    public void Verificar_es_de_un_solo_uso_con_el_reloj_de_la_base()
    {
        var cuerpo = Cuerpo("app_alta_verificar_correo");
        cuerpo.Should().Contain("pg_catalog.clock_timestamp()")
            .And.Contain("FOR UPDATE")
            .And.Contain("v_estado <> 'Iniciada'")
            .And.Contain("\"HashTokenVerificacion\" = NULL", "el enlace se gasta al verificar")
            .And.Contain("\"Estado\" = 'CorreoVerificado'")
            .And.NotContain("DELETE ")
            .And.NotContain("INSERT ")
            .And.NotContain("\"HashContrasena\"", "verificar el correo no lee el hash de la contraseña");

        // La caducidad se compara con la hora tomada tras el bloqueo, no con la de antes de esperar a otra petición.
        var bloqueo = cuerpo.IndexOf("FOR UPDATE", StringComparison.Ordinal);
        var reloj = cuerpo.IndexOf("v_ahora := pg_catalog.clock_timestamp()", StringComparison.Ordinal);
        reloj.Should().BeGreaterThan(bloqueo, "el reloj se lee después de FOR UPDATE");
        cuerpo.IndexOf("clock_timestamp", StringComparison.Ordinal).Should().Be(
            cuerpo.LastIndexOf("clock_timestamp", StringComparison.Ordinal), "no hay otra lectura del reloj, tampoco antes del bloqueo");
        cuerpo.IndexOf("v_ahora >=", StringComparison.Ordinal).Should().BeGreaterThan(reloj);
    }
}
