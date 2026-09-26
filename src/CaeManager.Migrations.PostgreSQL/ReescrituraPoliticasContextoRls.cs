namespace CaeManager.Migrations.PostgreSQL;

/// <summary>
/// P6, fase «contraer»: pasa TODAS las políticas RLS que leen los GUC sueltos
/// (<c>app.tenant_id</c>, <c>app.tenant_origen_id</c>, <c>app.usuario_id</c>)
/// al contexto validado de <c>app_contexto_validado()</c> (migración
/// <c>ContextoRlsFirmado</c>).
///
/// <para>
/// <b>Genérica a propósito.</b> Recorre <c>pg_policy</c> y reescribe la
/// expresión que PostgreSQL guarda (<c>pg_get_expr</c>), en vez de repetir a
/// mano más de cien <c>CREATE POLICY</c>: una lista escrita a mano se queda
/// corta en silencio el día que alguien añade una política; esto no, porque
/// termina comprobando que no queda ninguna leyendo <c>app.*</c> y aborta si
/// queda.
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
/// La fase «expandir» solo la ejecuta en los tests (sobre la base migrada)
/// para demostrar que cubre todas las formas que hay hoy; no se aplica en
/// ningún entorno hasta la migración de la fase «contraer».
/// </para>
/// </summary>
public static class ReescrituraPoliticasContextoRls
{
    public const string Sql = """
DO $reescritura$
DECLARE
    p record;
    nuevo_using text;
    nuevo_check text;
    sentencia text;
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
END
$reescritura$;
""";
}
