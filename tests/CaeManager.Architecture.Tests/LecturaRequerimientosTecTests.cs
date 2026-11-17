using System.Text.RegularExpressions;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La lectura inversa X-2 de la demo Inbound (P0a, <c>app_inbound_tec_requerimientos()</c>) sin base de datos: el texto de la
/// migración que una revisión podría romper sin ponerla en rojo en local. El comportamiento bajo <c>cae_app_runtime</c> lo
/// mide <c>RequerimientosTecBajoRuntimeTests</c> en la suite de integración (solo en CI).
/// </summary>
public class LecturaRequerimientosTecTests
{
    private static string Up => new AnadeLecturaRequerimientosTec()
        .UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Down => new AnadeLecturaRequerimientosTec()
        .DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Cuerpo
    {
        get
        {
            var inicio = Up.IndexOf("CREATE FUNCTION public.app_inbound_tec_requerimientos()", StringComparison.Ordinal);
            inicio.Should().BeGreaterThan(-1);
            return Up[inicio..Up.IndexOf("$$;", inicio, StringComparison.Ordinal)];
        }
    }

    [Fact]
    public void Es_SECURITY_DEFINER_con_search_path_fijo_y_solo_la_ejecuta_el_runtime()
    {
        Cuerpo.Should().Contain("SECURITY DEFINER").And.Contain("SET search_path TO 'pg_catalog', 'pg_temp'");
        Up.Should().Contain("REVOKE ALL ON FUNCTION public.app_inbound_tec_requerimientos() FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;")
            .And.Contain("GRANT EXECUTE ON FUNCTION public.app_inbound_tec_requerimientos() TO cae_app_runtime;");
        Regex.Matches(Up, @"GRANT\s").Should().ContainSingle("ningún otro rol recibe nada");
        Down.Should().Be("DROP FUNCTION IF EXISTS public.app_inbound_tec_requerimientos();");
    }

    [Fact]
    public void Sin_propietario_que_evite_la_RLS_falla_cerrada_y_sin_contexto_firmado_lanza_42501_antes_de_leer()
    {
        // El mismo orden que FalloCerradoLecturaInbound dejó en las vecinas: la guardia del propietario va la primera.
        var inicio = Cuerpo.IndexOf("BEGIN", StringComparison.Ordinal) + "BEGIN".Length;
        Cuerpo[inicio..].TrimStart().Should().StartWith("IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles");
        var guardia = Cuerpo.IndexOf("WHERE rolname = current_user AND (rolsuper OR rolbypassrls)", StringComparison.Ordinal);
        var contexto = Cuerpo.IndexOf("IF NOT COALESCE(public.app_ctx_valido(), false) OR public.app_ctx_tenant_id() IS NULL OR public.app_ctx_usuario_id() IS NULL THEN", StringComparison.Ordinal);
        var lectura = Cuerpo.IndexOf("RETURN QUERY", StringComparison.Ordinal);

        Cuerpo.Should().Contain("no evita la RLS").And.Contain("USING ERRCODE = '42501'");
        guardia.Should().BeGreaterThan(-1);
        contexto.Should().BeGreaterThan(guardia);
        lectura.Should().BeGreaterThan(contexto, "nada se lee antes de las dos comprobaciones");
    }

    [Fact]
    public void El_Tenant_TEC_sale_del_contexto_firmado_y_nunca_de_un_parametro_ni_de_la_GUC()
    {
        Cuerpo.Should().Contain("v_tec := public.app_ctx_tenant_id();")
            .And.Contain("WHERE c.\"TenantContratistaId\" = v_tec")
            .And.NotContain("current_setting(");
    }

    [Fact]
    public void Solo_contratos_activos_y_con_la_linea_Inbound_vigente_del_Tenant_TCE()
    {
        Cuerpo.Should().Contain("AND c.\"Estado\" = 'Activo'")
            .And.Contain("AND c.\"FechaCierreReal\" IS NULL")
            .And.Contain("AND NOT c.\"EstaEliminado\"")
            .And.Contain("WHERE pc.\"TenantId\" = c.\"TenantId\" AND pc.\"Linea\" = 'Inbound'")
            .And.Contain("AND pc.\"Estado\" = 'Vigente' AND NOT pc.\"EstaEliminado\"")
            .And.Contain("AND pc.\"VigenteDesdeUtc\" <= pg_catalog.clock_timestamp())");
    }

    [Fact]
    public void Solo_contratos_dentro_de_sus_fechas_el_dia_de_negocio_con_los_bordes_incluidos()
    {
        // El criterio de ContratoAcceso.VigenteEn sobre las fechas (opción C), con el día de negocio de las vecinas app_inbound_*.
        Cuerpo.Should().Contain("v_hoy := (pg_catalog.now() AT TIME ZONE 'Europe/Madrid')::date;")
            .And.Contain("AND c.\"FechaInicio\" <= v_hoy")
            .And.Contain("AND (c.\"FechaFin\" IS NULL OR v_hoy <= c.\"FechaFin\")")
            .And.NotContain("CURRENT_DATE").And.NotContain("current_date");
    }

    [Fact]
    public void Solo_las_capas_del_Centro_y_de_las_exigencias_del_Cliente_empresarial()
    {
        Cuerpo.Should().Contain("(p.\"Tipo\" = 'DeCentro' AND p.\"CentroId\" = c.\"CentroId\")")
            .And.Contain("(p.\"Tipo\" = 'ExigenciasClienteEmpresarial' AND p.\"ClienteEmpresarialId\" = c.\"ClienteEmpresarialId\")")
            .And.Contain("AND p.\"Activa\" AND NOT p.\"EstaEliminado\"")
            .And.Contain("r.\"PlantillaId\" = p.\"Id\" AND r.\"Activo\"")
            .And.NotContain("PorActividad").And.NotContain("PorRiesgo");
    }

    [Fact]
    public void Ninguna_columna_del_resultado_identifica_a_una_persona()
    {
        var columnas = Regex.Match(Cuerpo, @"RETURNS TABLE\((?<columnas>[^)]*)\)").Groups["columnas"].Value;

        Regex.Matches(columnas, "\"(?<nombre>[A-Za-z]+)\"").Select(m => m.Groups["nombre"].Value).Should().Equal(
            "ContratoAccesoId", "TenantTceNombre", "CentroId", "CentroNombre", "ClienteEmpresarialNombre", "RequisitoId",
            "TipoDocumentoId", "TipoDocumentoNombre", "Entidad", "Criterio", "ModoVigencia", "VigenciaMeses", "ToleranciaDias",
            "BloqueaAcceso", "AmbitoDocumento", "CapaPlantilla", "SePideA", "TiposContratacion");
        Cuerpo.Should().NotContainAny("Usuario", "Gestor", "Email", "Correo", "NombreCompleto", "Ejecutivo", "AspNetUsers",
            "Equivalencias", "Origen");
    }
}
