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

    private static string CuerpoReescrito() => Aplicar(
        CuerpoOriginal(), RegistroIncidenteDistingueSinUsuario.CambiosDeContexto.Concat(RegistroIncidenteDistingueSinUsuario.CambiosDeColumna));

    /// <summary>Lo que aplica el <c>Down</c>: los cambios de columna, al revés.</summary>
    private static string CuerpoTrasElDown() => Aplicar(
        CuerpoReescrito(), Enumerable.Reverse(RegistroIncidenteDistingueSinUsuario.CambiosDeColumna).Select(c => (c.Despues, c.Antes)));

    /// <summary>La búsqueda del incidente que absorbería la señal.</summary>
    private static string Agrupacion(string cuerpo)
    {
        var inicio = cuerpo.IndexOf("SELECT i.\"Id\" INTO v_existente", StringComparison.Ordinal);
        inicio.Should().BePositive();
        var fin = cuerpo.IndexOf("LIMIT 1;", inicio, StringComparison.Ordinal);
        return cuerpo[inicio..fin];
    }

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
        cuerpo.Should().Contain("' sin usuario en el contexto'");
    }

    [Fact]
    public void Agrupar_exige_la_columna_del_mismo_usuario_del_contexto_y_nunca_lee_la_evidencia()
    {
        var agrupacion = Agrupacion(CuerpoReescrito());

        agrupacion.Should().Contain("AND i.\"ContextoUsuarioId\" = v_ctx_usuario",
            "es una igualdad: sin usuario en el contexto, o con la columna nula, no coincide con nada");
        agrupacion.Should().NotContain("Evidencia",
            "la función anterior guardaba la evidencia que escribía quien llama: una clave fabricada plantaría una fila que suprime una alerta");
        agrupacion.Should().NotContain(" OR ", "una excepción dejaría agrupar a los registros de procedencia desconocida");
    }

    [Fact]
    public void La_columna_solo_la_rellena_la_funcion_con_el_usuario_del_contexto_y_lo_anterior_queda_nulo()
    {
        var cuerpo = CuerpoReescrito();

        cuerpo.Should().Contain("v_ctx_usuario := public.app_ctx_usuario_id();")
            .And.Contain("\"ConfirmadoEnUtc\", \"ContextoUsuarioId\")")
            .And.Contain("v_tenants, v_evidencia, 'Sospechado', NULL, v_ctx_usuario);");
        cuerpo.Should().NotContain("\"ContextoUsuarioId\" = i.").And.NotContain("\"ContextoUsuarioId\" = p_",
            "al agrupar no se reescribe, y ningún parámetro la alimenta");
        CuerpoOriginal().Should().NotContain("ContextoUsuarioId", "la función anterior no pudo escribirla, así que ninguna fila suya la trae");

        var columna = new RegistroIncidenteDistingueSinUsuario().UpOperations.OfType<AddColumnOperation>().Should().ContainSingle().Subject;
        columna.Table.Should().Be("IncidentesSeguridadSospechados");
        columna.IsNullable.Should().BeTrue();
        columna.DefaultValue.Should().BeNull("una columna nula no se puede confundir con un registro de la función corregida");
        columna.DefaultValueSql.Should().BeNull();
        columna.ComputedColumnSql.Should().BeNull();
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
    public void El_Down_no_restaura_la_vulnerabilidad_y_deja_de_agrupar()
    {
        var cuerpo = CuerpoTrasElDown();

        cuerpo.Should().NotBe(CuerpoOriginal());
        cuerpo.Should().Contain(RegistroIncidenteDistingueSinUsuario.GuardiaDeContextoFirmado, "sin firma se sigue rechazando")
            .And.Contain("sin contexto firmado' USING ERRCODE = '42501'")
            .And.Contain("p_evidencia - ARRAY['sujeto_distinto_del_contexto', 'contexto_usuario_id', 'sin_usuario_en_contexto', 'tenants_afectados_truncados']::text[]")
            .And.Contain("pg_catalog.jsonb_build_object('sin_usuario_en_contexto', true)")
            .And.NotContain("ContextoUsuarioId\"", "el Down quita la columna, y la función no puede seguir nombrándola");
        Agrupacion(cuerpo).Should().Contain("AND false", "sin la columna no queda nada de lo que fiarse: cada señal abre su incidente y su alerta")
            .And.NotContain("Evidencia", "la evidencia de las filas anteriores la pudo escribir quien llama");
    }

    [Fact]
    public void El_Down_reescribe_antes_de_quitar_la_columna_y_el_Up_se_puede_volver_a_aplicar()
    {
        var migracion = new RegistroIncidenteDistingueSinUsuario();

        migracion.DownOperations.Should().HaveCount(2);
        migracion.DownOperations[0].Should().BeOfType<SqlOperation>();
        migracion.DownOperations[1].Should().BeOfType<DropColumnOperation>()
            .Which.Name.Should().Be("ContextoUsuarioId");
        migracion.DownOperations.OfType<SqlOperation>().Single().Sql.Should()
            .Contain("AND false", "el Down solo cambia la agrupación y lo que escribe la columna")
            .And.NotContain("app_ctx_valido", "no toca el rechazo sin contexto firmado")
            .And.NotContain("sin_usuario_en_contexto", "no toca las marcas ni la retirada de las que llegan")
            .And.NotContain("sujeto_distinto_del_contexto", "no recupera la agrupación por evidencia");

        Aplicar(CuerpoTrasElDown(), RegistroIncidenteDistingueSinUsuario.CambiosDeColumna).Should().Be(CuerpoReescrito(),
            "partiendo del Down, el Up solo devuelve lo que necesita la columna");
        CuerpoOriginal().Should().NotContain(RegistroIncidenteDistingueSinUsuario.GuardiaDeContextoFirmado,
            "la guardia es lo que distingue los dos puntos de partida del Up");
        migracion.UpOperations.OfType<SqlOperation>().Single().Sql.Should()
            .Contain($"strpos(definicion, $texto${RegistroIncidenteDistingueSinUsuario.GuardiaDeContextoFirmado}$texto$) > 0");
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
