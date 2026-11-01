using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La migración reescribe por texto <c>app_registrar_incidente_seguridad</c>: conserva firma,
/// propietario, permisos, <c>SECURITY DEFINER</c> y <c>search_path</c> porque parte de
/// <c>pg_get_functiondef</c>. Aquí se aplica la misma sustitución sobre el cuerpo que crea
/// <see cref="AnadeIncidentesSeguridadSospechados"/>, el único que define la función, para ver sin
/// base de datos qué queda; el comportamiento bajo runtime lo prueba
/// <c>IncidentesSeguridadBajoRuntimeTests</c> en CI.
/// </summary>
public class RegistroIncidenteDistingueSinUsuarioTests
{
    private static string CuerpoOriginal()
    {
        var sql = new AnadeIncidentesSeguridadSospechados().UpOperations.OfType<SqlOperation>()
            .Select(o => o.Sql)
            .Single(s => s.Contains("CREATE FUNCTION public.app_registrar_incidente_seguridad"))
            .ReplaceLineEndings("\n");
        var inicio = sql.IndexOf("CREATE FUNCTION public.app_registrar_incidente_seguridad", StringComparison.Ordinal);
        var fin = sql.IndexOf("REVOKE ALL ON FUNCTION public.app_registrar_incidente_seguridad", StringComparison.Ordinal);
        return sql[inicio..fin];
    }

    private static string Aplicar(string cuerpo, IEnumerable<(string Antes, string Despues)> cambios)
    {
        foreach (var (antes, despues) in cambios)
        {
            var veces = cuerpo.Split(antes).Length - 1;
            veces.Should().Be(1, $"la migración aborta si «{antes}» no aparece exactamente una vez");
            cuerpo = cuerpo.Replace(antes, despues, StringComparison.Ordinal);
        }

        return cuerpo;
    }

    private static string CuerpoReescrito() => Aplicar(CuerpoOriginal(), RegistroIncidenteDistingueSinUsuario.Cambios);

    [Fact]
    public void Sin_contexto_firmado_se_rechaza_antes_de_tocar_nada()
    {
        var cuerpo = CuerpoReescrito();

        var guardia = cuerpo.IndexOf("IF NOT public.app_ctx_valido() THEN", StringComparison.Ordinal);
        guardia.Should().BePositive("sin firma no hay contexto en el que apoyar el registro");
        guardia.Should().BeLessThan(cuerpo.IndexOf("pg_advisory_xact_lock", StringComparison.Ordinal));
        cuerpo.Should().Contain("sin contexto firmado' USING ERRCODE = '42501'");
    }

    [Fact]
    public void La_deteccion_sin_usuario_se_registra_como_tal_y_nunca_agrupa_ni_es_agrupada()
    {
        var cuerpo = CuerpoReescrito();

        cuerpo.Should().Contain("v_sin_usuario := v_ctx_usuario IS NULL;");
        cuerpo.Should().Contain("pg_catalog.jsonb_build_object('sin_usuario_en_contexto', true)");
        cuerpo.Should().Contain("AND NOT v_sin_usuario AND NOT (i.\"Evidencia\" ? 'sin_usuario_en_contexto')",
            "ni abre un oráculo sobre incidentes ajenos, ni un registro sin usuario absorbe una señal real");
        cuerpo.Should().Contain("' sin usuario en el contexto'");
    }

    [Fact]
    public void Agrupar_exige_la_marca_del_mismo_usuario_del_contexto()
    {
        var cuerpo = CuerpoReescrito();

        cuerpo.Should().Contain("v_evidencia := v_evidencia || pg_catalog.jsonb_build_object('contexto_usuario_id', v_ctx_usuario::text);",
            "todo registro con usuario en el contexto lo guarda, coincida o no con el sujeto");
        cuerpo.Should().Contain("AND i.\"Evidencia\" ->> 'contexto_usuario_id' = v_ctx_usuario::text",
            "un registro anterior sin la marca es de procedencia desconocida y no agrupa");
        cuerpo.Should().NotContain("NOT v_distinto OR", "una excepción por sujeto coincidente dejaría agrupar a los registros sin marca");
    }

    [Fact]
    public void No_se_inventa_un_Actor()
    {
        var cuerpo = CuerpoReescrito();

        cuerpo.Should().Contain("VALUES (p_transicion_id, p_incidente_id, NULL, 'Sospechado', pg_catalog.now(), NULL,",
            "la primera transición la hace el sistema: ActorUsuarioId sigue nulo");
        cuerpo.Should().Contain("v_ctx_usuario := public.app_ctx_usuario_id();");
    }

    [Fact]
    public void Quien_llama_no_puede_fijar_las_marcas_que_pone_la_funcion()
    {
        var cuerpo = CuerpoReescrito();

        cuerpo.Should().Contain(
            "p_evidencia - ARRAY['sujeto_distinto_del_contexto', 'contexto_usuario_id', 'sin_usuario_en_contexto', 'tenants_afectados_truncados']::text[]");
    }

    [Fact]
    public void El_Down_devuelve_el_cuerpo_exacto()
    {
        var inversos = Enumerable.Reverse(RegistroIncidenteDistingueSinUsuario.Cambios).Select(c => (c.Despues, c.Antes));

        Aplicar(CuerpoReescrito(), inversos).Should().Be(CuerpoOriginal());
    }

    [Fact]
    public void La_migracion_reescribe_sin_tocar_permisos_ni_depender_del_fin_de_linea()
    {
        var migracion = new RegistroIncidenteDistingueSinUsuario();
        var up = migracion.UpOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        var down = migracion.DownOperations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;

        foreach (var sql in new[] { up, down })
        {
            sql.Should().Contain("public.app_registrar_incidente_seguridad(uuid,uuid,text,text,uuid[],jsonb,integer)")
                .And.Contain("pg_get_functiondef")
                .And.Contain("<> 1");
            sql.Should().NotContain("GRANT ").And.NotContain("REVOKE ").And.NotContain("DROP FUNCTION");
            sql.Should().NotContain("\r", "el Down busca el texto literal, y no puede depender de la compilación");
        }
    }
}
