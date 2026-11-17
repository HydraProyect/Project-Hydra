using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CaeManager.Application.Inbound.AutorizacionesLectura;
using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La lectura inversa X-2 de la demo Inbound (P0b, <c>app_inbound_tec_veredictos()</c>) sin base de datos: el texto de la
/// migración que una revisión podría romper sin ponerla en rojo en local, el DTO del puerto y quién lo pide. El
/// comportamiento bajo <c>cae_app_runtime</c> lo mide <c>VeredictosTecBajoRuntimeTests</c> en la suite de integración (solo
/// en CI).
/// </summary>
public class LecturaVeredictosTecTests
{
    // La causa y el motivo solo salen con Rechazada y cuando la base sabe que la evidencia no es de salud.
    private const string SoloSiNoEsDeSalud =
        "CASE WHEN e.\"Estado\" = 'Rechazada' AND NOT e.\"SoloConclusionAptitud\" AND td.\"Sensibilidad\" IN ('SinDatosPersonales', 'DatosPersonales')";

    private static string Up => new AnadeLecturaVeredictosTec()
        .UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Down => new AnadeLecturaVeredictosTec()
        .DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

    private static string Cuerpo => CuerpoDe(Up, "CREATE FUNCTION public.app_inbound_tec_veredictos()");

    [Fact]
    public void Es_SECURITY_DEFINER_con_search_path_fijo_y_solo_la_ejecuta_el_runtime()
    {
        Cuerpo.Should().Contain("SECURITY DEFINER").And.Contain("SET search_path TO 'pg_catalog', 'pg_temp'");
        Up.Should().Contain("REVOKE ALL ON FUNCTION public.app_inbound_tec_veredictos() FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;")
            .And.Contain("GRANT EXECUTE ON FUNCTION public.app_inbound_tec_veredictos() TO cae_app_runtime;");
        Regex.Matches(Up, @"GRANT\s").Should().ContainSingle("ningún otro rol recibe nada");
        Regex.Matches(Up, @"CREATE\s").Should().ContainSingle("la migración solo crea la función: ni tablas ni políticas");
        Down.Should().Be("DROP FUNCTION IF EXISTS public.app_inbound_tec_veredictos();");
    }

    [Fact]
    public void Sin_propietario_que_evite_la_RLS_falla_cerrada_y_sin_contexto_firmado_lanza_42501_antes_de_leer()
    {
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
    public void El_Tenant_TEC_sale_del_contexto_firmado_y_solo_salen_sus_propios_Documentos()
    {
        Cuerpo.Should().Contain("v_tec := public.app_ctx_tenant_id();")
            .And.Contain("WHERE e.\"TenantOrigenId\" = v_tec")
            .And.Contain("AND c.\"TenantContratistaId\" = v_tec")
            .And.Contain("AND NOT e.\"EstaEliminado\"")
            .And.Contain("JOIN public.\"ContratosAcceso\" c ON c.\"TenantId\" = e.\"TenantId\" AND c.\"Id\" = e.\"ContratoAccesoId\"")
            .And.NotContain("current_setting(");
    }

    [Fact]
    public void El_gate_D10_es_el_mismo_texto_que_el_de_los_requerimientos_P0a()
    {
        var p0a = CuerpoDe(
            new AnadeLecturaRequerimientosTec().UpOperations.OfType<SqlOperation>().Single().Sql,
            "CREATE FUNCTION public.app_inbound_tec_requerimientos()");

        var gate = GateD10(Cuerpo);
        gate.Should().Be(GateD10(p0a), "D10 es igual que en P0a: estado, cierre, baja, fechas y línea Inbound del Tenant TCE");
        gate.Should().Contain("AND c.\"Estado\" = 'Activo'").And.Contain("AND c.\"FechaCierreReal\" IS NULL")
            .And.Contain("AND pc.\"VigenteDesdeUtc\" <= pg_catalog.clock_timestamp())");
        Cuerpo.Should().Contain("v_hoy := (pg_catalog.now() AT TIME ZONE 'Europe/Madrid')::date;")
            .And.NotContain("CURRENT_DATE").And.NotContain("current_date");
    }

    [Fact]
    public void Una_evidencia_de_salud_no_da_causa_ni_motivo_y_ninguna_da_la_conclusion()
    {
        var normalizado = Regex.Replace(Cuerpo, @"\s+", " ");

        normalizado.Should().Contain(SoloSiNoEsDeSalud + " THEN e.\"CausaRechazo\"::text END,")
            .And.Contain(SoloSiNoEsDeSalud + " THEN e.\"MotivoRechazo\"::text END,")
            .And.Contain("CASE WHEN e.\"Estado\" = 'Rechazada' THEN COALESCE(e.\"RechazadaEnUtc\", e.\"ValidadaEnUtc\") ELSE e.\"ValidadaEnUtc\" END");
        Cuerpo.Should().NotContain("<> 'CategoriaEspecialSalud'", "la sensibilidad va por lista de admisión, que falla cerrada ante un valor nuevo");
        Regex.Matches(Cuerpo, "e\\.\"CausaRechazo\"").Should().ContainSingle("la causa no sale por ningún otro camino");
        Regex.Matches(Cuerpo, "e\\.\"MotivoRechazo\"").Should().ContainSingle("el motivo no sale por ningún otro camino");
        Cuerpo.Should().NotContain("\"ConclusionAptitud\"", "la conclusión de aptitud no sale de ninguna evidencia");
    }

    [Fact]
    public void Ninguna_columna_del_resultado_identifica_a_una_persona()
    {
        var columnas = Regex.Match(Cuerpo, @"RETURNS TABLE\((?<columnas>[^)]*)\)").Groups["columnas"].Value;

        Regex.Matches(columnas, "\"(?<nombre>[A-Za-z]+)\"").Select(m => m.Groups["nombre"].Value).Should().Equal(
            "ContratoAccesoId", "DocumentoOrigenId", "Estado", "CausaRechazo", "MotivoRechazo", "FechaVeredictoUtc");
        Cuerpo.Should().NotContainAny("Usuario", "Gestor", "Email", "Correo", "NombreCompleto", "Ejecutivo", "AspNetUsers",
            "Sujeto", "TrabajadorOrigenId", "ArchivoUrl", "HuellaSha256", "AutorizacionId");
    }

    [Fact]
    public void El_DTO_del_puerto_no_lleva_ninguna_identidad_de_Gestor_CAE()
    {
        typeof(VeredictoTecDto).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            "ContratoAccesoId", "DocumentoOrigenId", "Estado", "CausaRechazo", "MotivoRechazo", "FechaVeredictoUtc");
        typeof(VeredictoTecDto).GetProperties().Should().NotContain(
            p => p.Name.Contains("Usuario") || p.Name.Contains("Gestor") || p.Name.Contains("ValidadaPor") || p.Name.Contains("RechazadaPor"),
            "P-4: el Tenant TEC no sabe qué Gestor CAE validó o rechazó");
        Enum.GetNames<EstadoVeredictoTec>().Should().Equal("Aceptada", "Rechazada");
    }

    [Fact]
    public void Solo_la_implementacion_y_su_registro_nombran_el_puerto()
    {
        string[] ensamblados = ["CaeManager.Application", "CaeManager.Web", "CaeManager.Infrastructure"];

        ensamblados
            .Select(ReflexionArquitecturaHelper.CargarAssembly)
            .SelectMany(a => ReflexionArquitecturaHelper.TiposQueNombranTipoEnIl(a, typeof(ILectorVeredictosTec)))
            .Select(Exterior)
            .Where(t => t != typeof(ILectorVeredictosTec))
            .Distinct()
            .Should().BeEquivalentTo(
                [
                    typeof(CaeManager.Infrastructure.Inbound.Autorizacion.LectorVeredictosTecEnBase),
                    typeof(CaeManager.Infrastructure.DependencyInjection.InfrastructureServiceCollectionExtensions),
                ],
                "sin consumidor todavía: quien lo pida por primera vez lo añade aquí, en la PR que lo cablea");
    }

    private static string CuerpoDe(string sql, string cabecera)
    {
        var inicio = sql.IndexOf(cabecera, StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1);
        return sql[inicio..sql.IndexOf("$$;", inicio, StringComparison.Ordinal)];
    }

    // Del filtro de baja del contrato al cierre del EXISTS de la línea Inbound, con los espacios normalizados.
    private static string GateD10(string cuerpo)
    {
        const string desde = "AND NOT c.\"EstaEliminado\"";
        const string hasta = "pg_catalog.clock_timestamp())";
        var inicio = cuerpo.IndexOf(desde, StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(-1);
        var fin = cuerpo.IndexOf(hasta, inicio, StringComparison.Ordinal);
        fin.Should().BeGreaterThan(inicio);
        return Regex.Replace(cuerpo[inicio..(fin + hasta.Length)], @"\s+", " ");
    }

    // Los tipos que genera el compilador (máquinas de estado, clausuras) se cuentan como su tipo exterior.
    private static Type Exterior(Type tipo)
    {
        while (tipo.DeclaringType is { } exterior && tipo.IsDefined(typeof(CompilerGeneratedAttribute), false))
            tipo = exterior;
        return tipo;
    }
}
