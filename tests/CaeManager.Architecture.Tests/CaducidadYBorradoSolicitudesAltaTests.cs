using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La caducidad y el borrado de las Solicitudes de alta (A8, DA-7), fijados sobre el texto de la migración: dos
/// funciones <c>SECURITY DEFINER</c> acotadas, una por operación, con los plazos y los estados de origen escritos
/// dentro, y el borrado igual que <c>SolicitudAlta.BorrarDatosPersonales</c>. El comportamiento bajo el rol real lo
/// mide <c>LimpiezaSolicitudesAltaBajoRuntimeTests</c> en la suite de integración.
/// </summary>
public class CaducidadYBorradoSolicitudesAltaTests
{
    private const string FirmaCaducar = "app_alta_caducar_solicitudes(integer)";
    private const string FirmaBorrar = "app_alta_borrar_datos_personales(integer)";

    private static string Up => new CaducidadYBorradoSolicitudesAlta()
        .UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Down => new CaducidadYBorradoSolicitudesAlta()
        .DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Cuerpo(string nombre)
    {
        var inicio = Up.IndexOf($"CREATE FUNCTION public.{nombre}(", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1, nombre);
        return Up[inicio..Up.IndexOf("$$;", inicio, StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData("app_alta_caducar_solicitudes")]
    [InlineData("app_alta_borrar_datos_personales")]
    public void Cada_funcion_es_definidora_con_search_path_fijo_devuelve_un_recuento_y_trabaja_por_lotes(string nombre)
    {
        Cuerpo(nombre).Should().Contain("SECURITY DEFINER")
            .And.Contain("SET search_path TO 'pg_catalog', 'pg_temp'")
            .And.Contain("(p_lote integer) RETURNS integer")
            .And.Contain("p_lote < 1 OR p_lote > 1000")
            .And.Contain("LIMIT p_lote")
            .And.Contain("FOR UPDATE SKIP LOCKED", "no se pisa con el webhook de pago ni con otra instancia")
            .And.Contain("\"VersionFila\" = s.\"VersionFila\" + 1", "la transición incrementa el token de concurrencia")
            .And.Contain("rolsuper OR rolbypassrls", "avisa si el propietario no evita la RLS")
            .And.Contain("pg_catalog.clock_timestamp()", "decide con el reloj de la base")
            .And.NotContain("now()", "el reloj de la aplicación no entra, y el de la transacción tampoco");
    }

    [Fact]
    public void Solo_las_ejecuta_el_runtime_y_la_tabla_sigue_cerrada()
    {
        foreach (var firma in new[] { FirmaCaducar, FirmaBorrar })
        {
            Up.Should().Contain($"REVOKE ALL ON FUNCTION public.{firma} FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;")
                .And.Contain($"GRANT EXECUTE ON FUNCTION public.{firma} TO cae_app_runtime;");
            Down.Should().Contain($"DROP FUNCTION public.{firma};");
        }

        Up.Should().NotContain("CREATE POLICY", "la tabla sigue sin políticas")
            .And.NotContain("ON TABLE", "ningún privilegio de tabla para los roles de la aplicación")
            .And.NotContain("ON public.\"SolicitudesAlta\" TO", "ningún GRANT sobre la tabla")
            .And.NotContain("TO cae_app_soporte")
            .And.NotContain("TO cae_app_aprovisionamiento")
            .And.NotContain("TO PUBLIC");
    }

    [Fact]
    public void Caducar_solo_sale_de_los_cuatro_estados_del_dominio_y_con_el_plazo_vencido()
    {
        var cuerpo = Cuerpo("app_alta_caducar_solicitudes");

        cuerpo.Should().Contain("\"CaducaEnUtc\" <= v_ahora")
            .And.Contain("'Iniciada', 'CorreoVerificado', 'DatosCompletos'")
            .And.Contain("s.\"Estado\" = 'PendientePago'")
            .And.Contain("\"Estado\" = 'Caducada'")
            .And.NotContain("DELETE ")
            .And.NotContain("INSERT ")
            .And.NotContain("\"Correo\"", "caducar no borra datos personales");

        foreach (var final in new[] { "Pagada", "Completada", "Reembolsada", "DerivadaAAcceso", "Bloqueada" })
            cuerpo.Should().NotContain($"'{final}'", $"{final} no caduca");
    }

    [Fact]
    public void Borrar_solo_alcanza_lo_caducado_con_30_dias_desde_la_creacion_y_deja_traza()
    {
        var cuerpo = Cuerpo("app_alta_borrar_datos_personales");

        cuerpo.Should().Contain("interval '30 days'")
            .And.Contain("s.\"Estado\" = 'Caducada'")
            .And.Contain("s.\"DatosPersonalesBorradosEnUtc\" IS NULL", "repetirlo no cambia nada")
            .And.Contain("s.\"CreadaEnUtc\" <= v_corte")
            .And.Contain("DELETE FROM public.\"AceptacionesSolicitudAlta\"")
            .And.Contain("\"DatosPersonalesBorradosEnUtc\" = v_ahora")
            .And.Contain("INSERT INTO public.\"TrazasPurga\"")
            .And.NotContain("SET \"Estado\"", "borrar los datos no es una transición de estado");
    }

    [Fact]
    public void Borrar_deja_a_NULL_los_mismos_campos_que_BorrarDatosPersonales_del_dominio()
    {
        var enSql = Regex.Matches(Cuerpo("app_alta_borrar_datos_personales"), "\"(?<columna>\\w+)\" = NULL")
            .Select(m => m.Groups["columna"].Value).ToHashSet();

        var enDominio = PropiedadesQueElDominioDejaANull();

        enSql.Should().BeEquivalentTo(enDominio,
            "si el dominio añade un dato personal, la función SQL tiene que borrarlo también (y al revés)");
    }

    [Fact]
    public void La_traza_acepta_el_nuevo_tipo_de_purga_y_revertir_no_descarta_evidencia()
    {
        Up.Should().Contain("ADD CONSTRAINT \"CK_TrazasPurga_Purga\"")
            .And.Contain("'IniciosSesion', 'SesionesUsuario', 'RegistrosAuditoria', 'SolicitudesAlta'");
        Up.Should().Contain("pg_catalog.has_table_privilege(p.proowner, 'public.\"TrazasPurga\"', 'INSERT')",
            "un propietario que no pueda escribir la traza debe parar la migración, no el trabajo en producción");
        Down.Should().Contain("WHERE \"Purga\" = 'SolicitudesAlta'").And.Contain("RAISE EXCEPTION");
    }

    /// <summary>
    /// Ejecuta el dominio sobre dos Solicitudes de alta (una que caduca en <c>Iniciada</c> y otra en
    /// <c>PendientePago</c>, con todos los datos rellenos) y devuelve las columnas que <c>BorrarDatosPersonales</c> deja
    /// a null, sin la fecha de borrado, que es la que lo anota.
    /// </summary>
    private static HashSet<string> PropiedadesQueElDominioDejaANull()
    {
        var ahora = DateTime.UtcNow;
        var token = new string('t', 40);

        var iniciada = SolicitudAlta.Iniciar("persona@empresa.test", "AQAAAAIAAYagAAAAEHash", token, "203.0.113.9", "Navegador", ahora);

        var pendiente = SolicitudAlta.Iniciar("otra@empresa.test", "AQAAAAIAAYagAAAAEHash", token, "203.0.113.9", "Navegador", ahora);
        pendiente.VerificarCorreo(token, ahora).Should().BeTrue();
        pendiente.CompletarDatos("Empresa SL", "B12345674", "Calle Falsa 1", TramoTrabajadoresActivos.De1A10, true, false, ahora, "600 000 000");
        pendiente.PasarAPendientePago(
            ModalidadSuscripcionAlta.Mensual,
            [new AceptacionDeclarada("Condiciones", "2026-11", new string('a', 64), true, "203.0.113.9", ahora)],
            "cs_test_1", ahora);

        var modificadas = new HashSet<string>();
        foreach (var solicitud in new[] { iniciada, pendiente })
        {
            solicitud.Caducar(ahora + SolicitudAlta.PlazoCaducidad);
            var propiedades = typeof(SolicitudAlta).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != nameof(SolicitudAlta.Aceptaciones) && Admite_null(p.PropertyType)).ToList();
            var antes = propiedades.ToDictionary(p => p.Name, p => p.GetValue(solicitud));

            solicitud.BorrarDatosPersonales(ahora + TimeSpan.FromDays(30));

            foreach (var p in propiedades.Where(p => antes[p.Name] is not null && p.GetValue(solicitud) is null))
                modificadas.Add(p.Name);
            solicitud.Aceptaciones.Should().BeEmpty();
        }

        return modificadas;

        static bool Admite_null(Type tipo) => !tipo.IsValueType || Nullable.GetUnderlyingType(tipo) is not null;
    }
}
