namespace CaeManager.Migrations.PostgreSQL;

/// <summary>
/// P6, fase «contraer» (migración <c>ContraeContextoRlsFirmado</c>): pasa TODAS
/// las políticas RLS y las funciones de <c>public</c> que leen los GUC sueltos
/// (<c>app.tenant_id</c>, <c>app.tenant_origen_id</c>, <c>app.usuario_id</c>)
/// al contexto validado de <c>app_contexto_validado()</c> (migración
/// <c>ContextoRlsFirmado</c>, hoy en la línea base). Un GUC suelto lo puede fijar
/// cualquiera que ejecute SQL en la sesión; el token de <c>app.contexto</c> solo
/// lo firma la aplicación, para el backend de esa conexión y con caducidad.
///
/// <para>
/// <b>Genérica a propósito.</b> Recorre <c>pg_policy</c> y reescribe la
/// expresión que PostgreSQL guarda (<c>pg_get_expr</c>), en vez de repetir a
/// mano más de cien <c>CREATE POLICY</c>: una lista escrita a mano se queda
/// corta en silencio el día que alguien añade una política; esto no, porque
/// termina comprobando que no queda ninguna leyendo <c>app.*</c> y aborta si
/// queda. Las particiones de eventos tienen sus propias filas en
/// <c>pg_policy</c>, así que también se reescriben, y las que se creen después
/// copian la expresión ya reescrita del padre (<c>ParticionadoMensualEventos</c>).
/// </para>
///
/// <para>
/// <b>Solo toca las cláusulas que la política ya tenía</b> (hallazgo A4 de
/// Codex sobre el diseño): una política <c>FOR SELECT</c> no admite
/// <c>WITH CHECK</c> y una <c>FOR INSERT</c> no admite <c>USING</c>.
/// <c>ALTER POLICY</c> conserva el comando, los roles y el carácter
/// permisivo o restrictivo.
/// </para>
///
/// <para>
/// Cada lectura pasa a <c>(SELECT app_ctx_x())</c>: PostgreSQL lo evalúa como
/// InitPlan, una vez por aparición en la consulta y no una vez por fila. La
/// excepción de bootstrap (<c>app.usuario_id</c> vacío, «nadie autenticado»)
/// pasa a «contexto firmado válido y sin usuario»: sin token no hay nada.
/// </para>
///
/// <para>
/// <b>Funciones.</b> Las funciones de <c>public</c> cuyo cuerpo lee un GUC
/// suelto (hoy solo <c>app_enmascarar_auditoria_de_trabajador_anonimizado</c>,
/// SECURITY DEFINER) se redefinen con <c>pg_get_functiondef</c> cambiando la
/// lectura por <c>public.app_ctx_x()</c> (cualificada: esas funciones fijan
/// <c>search_path</c> a <c>pg_catalog, pg_temp</c>). <c>CREATE OR REPLACE</c>
/// conserva propietario y permisos. La sustitución lleva la marca
/// <see cref="MarcaFuncion"/> para que <see cref="SqlInversa"/> deshaga solo lo
/// que hizo esta migración y no toque las funciones que ya nacieron leyendo el
/// contexto firmado.
/// </para>
/// </summary>
public static class ReescrituraPoliticasContextoRls
{
    /// <summary>Comentario SQL que marca en el cuerpo de una función la lectura que reescribió esta migración.</summary>
    public const string MarcaFuncion = "/* P6 contraer */";

    public const string Sql = """
DO $reescritura$
DECLARE
    p record;
    f record;
    nuevo_using text;
    nuevo_check text;
    sentencia text;
    definicion text;
    restantes text;
BEGIN
    FOR p IN
        SELECT pol.polname, n.nspname, cl.relname,
               pg_get_expr(pol.polqual, pol.polrelid) AS expr_using,
               pg_get_expr(pol.polwithcheck, pol.polrelid) AS expr_check
          FROM pg_policy pol
          JOIN pg_class cl ON cl.oid = pol.polrelid
          JOIN pg_namespace n ON n.oid = cl.relnamespace
         WHERE coalesce(pg_get_expr(pol.polqual, pol.polrelid), '')
               || coalesce(pg_get_expr(pol.polwithcheck, pol.polrelid), '') LIKE '%current_setting(''app.%'
    LOOP
        nuevo_using := p.expr_using;
        nuevo_check := p.expr_check;

        nuevo_using := regexp_replace(nuevo_using,
            'NULLIF\(current_setting\(''app\.usuario_id''::text, true\), ''''::text\) IS NULL',
            '((SELECT app_ctx_valido()) AND ((SELECT app_ctx_usuario_id()) IS NULL))', 'g');
        nuevo_check := regexp_replace(nuevo_check,
            'NULLIF\(current_setting\(''app\.usuario_id''::text, true\), ''''::text\) IS NULL',
            '((SELECT app_ctx_valido()) AND ((SELECT app_ctx_usuario_id()) IS NULL))', 'g');

        nuevo_using := regexp_replace(nuevo_using,
            '\(NULLIF\(current_setting\(''app\.(tenant_id|tenant_origen_id|usuario_id)''::text, true\), ''''::text\)\)::uuid',
            '(SELECT app_ctx_\1())', 'g');
        nuevo_check := regexp_replace(nuevo_check,
            '\(NULLIF\(current_setting\(''app\.(tenant_id|tenant_origen_id|usuario_id)''::text, true\), ''''::text\)\)::uuid',
            '(SELECT app_ctx_\1())', 'g');

        sentencia := format('ALTER POLICY %I ON %I.%I', p.polname, p.nspname, p.relname);
        IF nuevo_using IS NOT NULL THEN
            sentencia := sentencia || ' USING (' || nuevo_using || ')';
        END IF;
        IF nuevo_check IS NOT NULL THEN
            sentencia := sentencia || ' WITH CHECK (' || nuevo_check || ')';
        END IF;
        EXECUTE sentencia;
    END LOOP;

    SELECT string_agg(format('%I.%I', cl.relname, pol.polname), ', ') INTO restantes
      FROM pg_policy pol
      JOIN pg_class cl ON cl.oid = pol.polrelid
     WHERE coalesce(pg_get_expr(pol.polqual, pol.polrelid), '')
           || coalesce(pg_get_expr(pol.polwithcheck, pol.polrelid), '') LIKE '%current_setting(''app.%';
    IF restantes IS NOT NULL THEN
        RAISE EXCEPTION 'P6: políticas que siguen leyendo app.* tras la reescritura: %', restantes;
    END IF;

    FOR f IN
        SELECT pr.oid
          FROM pg_proc pr
          JOIN pg_namespace n ON n.oid = pr.pronamespace
         WHERE n.nspname = 'public'
           AND pr.prosrc ~ 'current_setting\(''app\.(tenant_id|tenant_origen_id|usuario_id)'''
    LOOP
        definicion := regexp_replace(pg_get_functiondef(f.oid),
            'NULLIF\(current_setting\(''app\.(tenant_id|tenant_origen_id|usuario_id)'', true\), ''''\)::uuid',
            'public.app_ctx_\1() /* P6 contraer */', 'g');
        EXECUTE definicion;
    END LOOP;

    SELECT string_agg(pr.proname, ', ') INTO restantes
      FROM pg_proc pr
      JOIN pg_namespace n ON n.oid = pr.pronamespace
     WHERE n.nspname = 'public'
       AND pr.prosrc ~ 'current_setting\(''app\.(tenant_id|tenant_origen_id|usuario_id)''';
    IF restantes IS NOT NULL THEN
        RAISE EXCEPTION 'P6: funciones que siguen leyendo app.* tras la reescritura: %', restantes;
    END IF;
END
$reescritura$;
""";

    /// <summary>
    /// El <c>Down</c> de <c>ContraeContextoRlsFirmado</c>: la sustitución
    /// inversa, sobre la expresión que PostgreSQL deparsea tras <see cref="Sql"/>
    /// (<c>( SELECT app_ctx_x() AS app_ctx_x)</c>). Antes de «contraer» ninguna
    /// política leía <c>app_ctx_*</c>, así que todas vuelven al GUC suelto, y
    /// aborta si queda alguna. En funciones solo deshace las lecturas con
    /// <see cref="MarcaFuncion"/>.
    /// </summary>
    public const string SqlInversa = """
DO $reescritura_inversa$
DECLARE
    p record;
    f record;
    nuevo_using text;
    nuevo_check text;
    sentencia text;
    definicion text;
    restantes text;
BEGIN
    FOR p IN
        SELECT pol.polname, n.nspname, cl.relname,
               pg_get_expr(pol.polqual, pol.polrelid) AS expr_using,
               pg_get_expr(pol.polwithcheck, pol.polrelid) AS expr_check
          FROM pg_policy pol
          JOIN pg_class cl ON cl.oid = pol.polrelid
          JOIN pg_namespace n ON n.oid = cl.relnamespace
         WHERE coalesce(pg_get_expr(pol.polqual, pol.polrelid), '')
               || coalesce(pg_get_expr(pol.polwithcheck, pol.polrelid), '') LIKE '%app_ctx_%'
    LOOP
        nuevo_using := p.expr_using;
        nuevo_check := p.expr_check;

        nuevo_using := regexp_replace(nuevo_using,
            '\(\(\s*SELECT\s+(public\.)?app_ctx_valido\(\)(\s+AS\s+app_ctx_valido)?\)\s+AND\s+\(\(\s*SELECT\s+(public\.)?app_ctx_usuario_id\(\)(\s+AS\s+app_ctx_usuario_id)?\)\s+IS\s+NULL\)\)',
            'NULLIF(current_setting(''app.usuario_id''::text, true), ''''::text) IS NULL', 'g');
        nuevo_check := regexp_replace(nuevo_check,
            '\(\(\s*SELECT\s+(public\.)?app_ctx_valido\(\)(\s+AS\s+app_ctx_valido)?\)\s+AND\s+\(\(\s*SELECT\s+(public\.)?app_ctx_usuario_id\(\)(\s+AS\s+app_ctx_usuario_id)?\)\s+IS\s+NULL\)\)',
            'NULLIF(current_setting(''app.usuario_id''::text, true), ''''::text) IS NULL', 'g');

        nuevo_using := regexp_replace(nuevo_using,
            '\(\s*SELECT\s+(public\.)?app_ctx_(tenant_id|tenant_origen_id|usuario_id)\(\)(\s+AS\s+app_ctx_\w+)?\)',
            '(NULLIF(current_setting(''app.\2''::text, true), ''''::text))::uuid', 'g');
        nuevo_check := regexp_replace(nuevo_check,
            '\(\s*SELECT\s+(public\.)?app_ctx_(tenant_id|tenant_origen_id|usuario_id)\(\)(\s+AS\s+app_ctx_\w+)?\)',
            '(NULLIF(current_setting(''app.\2''::text, true), ''''::text))::uuid', 'g');

        sentencia := format('ALTER POLICY %I ON %I.%I', p.polname, p.nspname, p.relname);
        IF nuevo_using IS NOT NULL THEN
            sentencia := sentencia || ' USING (' || nuevo_using || ')';
        END IF;
        IF nuevo_check IS NOT NULL THEN
            sentencia := sentencia || ' WITH CHECK (' || nuevo_check || ')';
        END IF;
        EXECUTE sentencia;
    END LOOP;

    SELECT string_agg(format('%I.%I', cl.relname, pol.polname), ', ') INTO restantes
      FROM pg_policy pol
      JOIN pg_class cl ON cl.oid = pol.polrelid
     WHERE coalesce(pg_get_expr(pol.polqual, pol.polrelid), '')
           || coalesce(pg_get_expr(pol.polwithcheck, pol.polrelid), '') LIKE '%app_ctx_%';
    IF restantes IS NOT NULL THEN
        RAISE EXCEPTION 'P6: políticas que siguen leyendo app_ctx_* tras deshacer la reescritura: %', restantes;
    END IF;

    FOR f IN
        SELECT pr.oid
          FROM pg_proc pr
          JOIN pg_namespace n ON n.oid = pr.pronamespace
         WHERE n.nspname = 'public'
           AND strpos(pr.prosrc, '/* P6 contraer */') > 0
    LOOP
        definicion := regexp_replace(pg_get_functiondef(f.oid),
            'public\.app_ctx_(tenant_id|tenant_origen_id|usuario_id)\(\) /\* P6 contraer \*/',
            'NULLIF(current_setting(''app.\1'', true), '''')::uuid', 'g');
        EXECUTE definicion;
    END LOOP;
END
$reescritura_inversa$;
""";
}
