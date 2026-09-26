namespace CaeManager.Migrations.PostgreSQL;

/// <summary>
/// P1-M2: particionado mensual por rango de fecha de los dos registros de
/// auditoría del Tenant propietario (<c>RegistrosAuditoria</c> por
/// <c>FechaUtc</c> y <c>RegistrosAccesoDocumentoSensible</c> por
/// <c>OcurridoEnUtc</c>), y la creación automática de sus particiones futuras.
///
/// <para>
/// <b>Por qué SQL genérico y no columnas escritas a mano.</b> La tabla madre se
/// crea con <c>LIKE</c> de la tabla previa, los índices se recrean desde
/// <c>pg_get_indexdef</c>, los privilegios de los roles de aplicación se copian
/// leyendo <c>has_table_privilege</c> y las políticas RLS se copian leyendo
/// <c>pg_policy</c>. Una lista a mano se quedaría corta en silencio el día que
/// alguien añada un índice o cambie una política (la fase «contraer» de P6
/// reescribe todas las expresiones); copiar lo que hay no.
/// </para>
///
/// <para>
/// <b>Privilegios y RLS en cada partición.</b> Una consulta a través de la madre
/// comprueba solo los privilegios y las políticas de la madre, pero una consulta
/// que nombre la partición comprueba los de la partición. Y cada tabla nueva
/// recibe por <c>ALTER DEFAULT PRIVILEGES</c> (<c>HabilitarRlsPostgres</c>,
/// <c>RolSoporteSoloLectura</c>) los cuatro verbos para <c>cae_app_runtime</c> y
/// SELECT para <c>cae_app_soporte</c>: sin retirarlos, runtime podría borrar
/// auditoría nombrando la partición y saltarse <c>AuditoriaSoloInsercionParaRuntime</c>.
/// Por eso cada partición pierde todo privilegio de aplicación y lleva RLS
/// forzada con las mismas políticas que la madre.
/// </para>
///
/// <para>
/// <b>Partición por defecto.</b> Un evento con una fecha fuera de las
/// particiones creadas (reloj desviado, dato importado, creación automática
/// parada) cae en <c>_pdefecto</c> en vez de hacer fallar la transacción de
/// negocio que lo produjo. Al crear un mes que tenga filas en la partición por
/// defecto, <c>app_particion_eventos_asegurar</c> las mueve a la partición
/// nueva en la misma transacción; si no, <c>CREATE TABLE ... PARTITION OF</c>
/// fallaría.
/// </para>
///
/// <para>
/// <b>El rol que ejecuta evita la RLS.</b> Copiar la tabla y mover filas de la
/// partición por defecto tienen que ver las filas de todos los Tenants. Con
/// <c>FORCE ROW LEVEL SECURITY</c>, un propietario que no sea superusuario ni
/// <c>BYPASSRLS</c> vería cero filas sin contexto de Tenant, y la copia
/// «cuadraría» con cero filas perdidas. Las dos rutas lo comprueban y abortan
/// en vez de confiar en ello (hoy el migrador y el propietario de las pruebas
/// son <c>postgres</c>).
/// </para>
///
/// <para>
/// Sin purga: nada aquí borra una partición. La retención de la auditoría es una
/// decisión pendiente (P1-M2, DECISIÓN NECESARIA de 2026-09-26) y, cuando se
/// tome, la ejecutará un rol propio, nunca <c>cae_app_runtime</c>.
/// </para>
/// </summary>
public static class ParticionadoMensualEventos
{
    /// <summary>Meses por delante que deja creados la migración y que asegura el servicio diario.</summary>
    public const int MesesPorDelante = 3;

    /// <summary>Límite de <c>app_asegurar_particiones_eventos</c>: un valor mayor se recorta.</summary>
    public const int MesesPorDelanteMaximo = 24;

    /// <summary>Las tablas particionadas y su columna de partición.</summary>
    public static readonly IReadOnlyList<(string Tabla, string Columna)> Tablas =
    [
        ("RegistrosAuditoria", "FechaUtc"),
        ("RegistrosAccesoDocumentoSensible", "OcurridoEnUtc"),
    ];

    public const string SufijoDefecto = "_pdefecto";

    private const string RolesAplicacion = "cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento";

    private const string GuardaEvitaRls = """
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
        RAISE EXCEPTION 'P1-M2: el rol % no evita la RLS; copiar o mover eventos bajo FORCE ROW LEVEL SECURITY los filtraría sin error', current_user;
    END IF;
""";

    /// <summary>
    /// Las cuatro funciones. Solo <c>app_asegurar_particiones_eventos</c> es
    /// ejecutable por un rol de aplicación (<c>cae_app_runtime</c>): no recibe
    /// nombres ni SQL, solo cuántos meses por delante, recortado a
    /// <see cref="MesesPorDelanteMaximo"/>, y recorre una lista fija de tablas.
    /// </summary>
    public static readonly string CrearFuncionesSql = $$"""
CREATE OR REPLACE FUNCTION public.app_rls_copiar_politicas(p_origen regclass, p_destino regclass) RETURNS void
  LANGUAGE plpgsql VOLATILE
  SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    pol record;
    sentencia text;
BEGIN
    FOR pol IN
        SELECT polname, polcmd, polpermissive, polroles,
               pg_get_expr(polqual, polrelid) AS expr_using,
               pg_get_expr(polwithcheck, polrelid) AS expr_check
          FROM pg_policy
         WHERE polrelid = p_origen
    LOOP
        EXECUTE format('DROP POLICY IF EXISTS %I ON %s', pol.polname, p_destino);
        sentencia := format('CREATE POLICY %I ON %s AS %s FOR %s TO %s',
            pol.polname, p_destino,
            CASE WHEN pol.polpermissive THEN 'PERMISSIVE' ELSE 'RESTRICTIVE' END,
            CASE pol.polcmd WHEN 'r' THEN 'SELECT' WHEN 'a' THEN 'INSERT' WHEN 'w' THEN 'UPDATE'
                            WHEN 'd' THEN 'DELETE' ELSE 'ALL' END,
            CASE WHEN pol.polroles = '{0}'::oid[] THEN 'PUBLIC'
                 ELSE (SELECT string_agg(quote_ident(rolname), ', ') FROM pg_roles WHERE oid = ANY (pol.polroles)) END);
        IF pol.expr_using IS NOT NULL THEN
            sentencia := sentencia || ' USING (' || pol.expr_using || ')';
        END IF;
        IF pol.expr_check IS NOT NULL THEN
            sentencia := sentencia || ' WITH CHECK (' || pol.expr_check || ')';
        END IF;
        EXECUTE sentencia;
    END LOOP;
END
$$;

CREATE OR REPLACE FUNCTION public.app_particion_eventos_proteger(p_particion regclass, p_madre regclass) RETURNS void
  LANGUAGE plpgsql VOLATILE
  SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    EXECUTE format('REVOKE ALL ON %s FROM PUBLIC, {{RolesAplicacion}}', p_particion);
    EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', p_particion);
    EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', p_particion);
    PERFORM public.app_rls_copiar_politicas(p_madre, p_particion);
END
$$;

CREATE OR REPLACE FUNCTION public.app_particion_eventos_asegurar(p_madre regclass, p_mes date) RETURNS boolean
  LANGUAGE plpgsql VOLATILE
  SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_nombre_madre text;
    v_columna text;
    v_desde timestamptz;
    v_hasta timestamptz;
    v_nombre text;
    v_defecto regclass;
    v_movidas bigint := 0;
    v_reinsertadas bigint := 0;
BEGIN
{{GuardaEvitaRls}}
    SELECT c.relname INTO v_nombre_madre
      FROM pg_class c
     WHERE c.oid = p_madre AND c.relkind = 'p';
    IF v_nombre_madre IS NULL THEN
        RAISE EXCEPTION 'P1-M2: % no es una tabla particionada', p_madre;
    END IF;

    SELECT a.attname INTO v_columna
      FROM pg_partitioned_table pt
      JOIN pg_attribute a ON a.attrelid = pt.partrelid AND a.attnum = pt.partattrs[0]
     WHERE pt.partrelid = p_madre;

    -- Límites en UTC, sin depender del TimeZone de la sesión: date_trunc sobre
    -- timestamp (sin zona) y conversión explícita.
    v_desde := date_trunc('month', p_mes::timestamp) AT TIME ZONE 'UTC';
    v_hasta := (date_trunc('month', p_mes::timestamp) + interval '1 month') AT TIME ZONE 'UTC';
    v_nombre := v_nombre_madre || '_p' || to_char(date_trunc('month', p_mes::timestamp), 'YYYYMM');

    IF to_regclass(format('public.%I', v_nombre)) IS NOT NULL THEN
        RETURN false;
    END IF;

    -- Filas del mes que cayeron en la partición por defecto: se apartan antes
    -- de crear la partición (si no, el CREATE falla) y se reinsertan en ella,
    -- todo en la misma transacción.
    v_defecto := to_regclass(format('public.%I', v_nombre_madre || '{{SufijoDefecto}}'));
    IF v_defecto IS NOT NULL THEN
        -- El bloqueo que el CREATE ... PARTITION OF tomaría de todos modos, pero
        -- ANTES de apartar las filas (hallazgo de Codex): si no, una inserción
        -- concurrente de ese mes que confirme entre el DELETE y el CREATE deja
        -- una fila en la partición por defecto que hace fallar el CREATE.
        EXECUTE format('LOCK TABLE %s IN ACCESS EXCLUSIVE MODE', p_madre);
        EXECUTE format('CREATE TEMP TABLE particion_eventos_movidas (LIKE %s) ON COMMIT DROP', p_madre);
        EXECUTE format(
            'WITH m AS (DELETE FROM %s WHERE %I >= %L AND %I < %L RETURNING *) INSERT INTO pg_temp.particion_eventos_movidas SELECT * FROM m',
            v_defecto, v_columna, v_desde, v_columna, v_hasta);
        GET DIAGNOSTICS v_movidas = ROW_COUNT;
    END IF;

    EXECUTE format('CREATE TABLE public.%I PARTITION OF %s FOR VALUES FROM (%L) TO (%L)',
        v_nombre, p_madre, v_desde, v_hasta);
    PERFORM public.app_particion_eventos_proteger(format('public.%I', v_nombre)::regclass, p_madre);

    IF v_defecto IS NOT NULL THEN
        IF v_movidas > 0 THEN
            EXECUTE format('INSERT INTO public.%I SELECT * FROM pg_temp.particion_eventos_movidas', v_nombre);
            GET DIAGNOSTICS v_reinsertadas = ROW_COUNT;
            IF v_reinsertadas <> v_movidas THEN
                RAISE EXCEPTION 'P1-M2: se apartaron % eventos de % y se reinsertaron %', v_movidas, v_defecto, v_reinsertadas;
            END IF;
            RAISE WARNING 'P1-M2: % eventos de % movidos a la partición nueva %', v_movidas, v_defecto, v_nombre;
        END IF;
        DROP TABLE pg_temp.particion_eventos_movidas;
    END IF;

    RETURN true;
END
$$;

CREATE OR REPLACE FUNCTION public.app_asegurar_particiones_eventos(p_meses_adelante integer)
  RETURNS TABLE (particiones_creadas integer, eventos_en_defecto bigint)
  LANGUAGE plpgsql VOLATILE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_tabla text;
    v_meses integer := least(greatest(coalesce(p_meses_adelante, 0), 0), {{MesesPorDelanteMaximo}});
    v_mes_actual date := date_trunc('month', now() AT TIME ZONE 'UTC')::date;
    v_defecto regclass;
    v_filas bigint;
BEGIN
    -- Dos réplicas a la vez no se pisan: la segunda espera y encuentra hecho.
    PERFORM pg_advisory_xact_lock(hashtextextended('app_asegurar_particiones_eventos', 0));
    particiones_creadas := 0;
    eventos_en_defecto := 0;
    FOREACH v_tabla IN ARRAY ARRAY[{{string.Join(", ", Tablas.Select(t => $"'{t.Tabla}'"))}}] LOOP
        FOR i IN 0..v_meses LOOP
            IF public.app_particion_eventos_asegurar(
                   format('public.%I', v_tabla)::regclass,
                   (v_mes_actual + make_interval(months => i))::date) THEN
                particiones_creadas := particiones_creadas + 1;
            END IF;
        END LOOP;
        v_defecto := to_regclass(format('public.%I', v_tabla || '{{SufijoDefecto}}'));
        IF v_defecto IS NOT NULL THEN
            EXECUTE format('SELECT count(*) FROM %s', v_defecto) INTO v_filas;
            eventos_en_defecto := eventos_en_defecto + v_filas;
        END IF;
    END LOOP;
    RETURN NEXT;
END
$$;

REVOKE ALL ON FUNCTION public.app_rls_copiar_politicas(regclass, regclass) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.app_particion_eventos_proteger(regclass, regclass) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.app_particion_eventos_asegurar(regclass, date) FROM PUBLIC;
REVOKE ALL ON FUNCTION public.app_asegurar_particiones_eventos(integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.app_asegurar_particiones_eventos(integer) TO cae_app_runtime;
""";

    public const string EliminarFuncionesSql = """
DROP FUNCTION IF EXISTS public.app_asegurar_particiones_eventos(integer);
DROP FUNCTION IF EXISTS public.app_particion_eventos_asegurar(regclass, date);
DROP FUNCTION IF EXISTS public.app_particion_eventos_proteger(regclass, regclass);
DROP FUNCTION IF EXISTS public.app_rls_copiar_politicas(regclass, regclass);
""";

    /// <summary>
    /// Convierte <paramref name="tabla"/> en tabla particionada por
    /// <paramref name="columna"/> sin perder un evento: aparta la previa, crea la
    /// madre con PK <c>(Id, columna)</c>, crea los meses desde el evento más
    /// antiguo (como mucho diez años atrás; lo anterior cae en la partición por
    /// defecto) hasta el actual más <see cref="MesesPorDelante"/>, copia, y
    /// aborta la migración entera si el recuento o la suma de hashes por fila no
    /// coinciden. Requiere <see cref="CrearFuncionesSql"/> antes.
    /// </summary>
    public static string ParticionarSql(string tabla, string columna) => $$"""
DO $particionado$
DECLARE
    v_indices text[];
    v_def text;
    v_min timestamptz;
    v_mes date;
    v_fin date;
    v_filas_previa bigint;
    v_hash_previa numeric;
    v_filas_nueva bigint;
    v_hash_nueva numeric;
    r record;
BEGIN
{{GuardaEvitaRls}}
    IF (SELECT relkind FROM pg_class WHERE oid = 'public."{{tabla}}"'::regclass) <> 'r' THEN
        RAISE EXCEPTION 'P1-M2: "{{tabla}}" ya no es una tabla ordinaria';
    END IF;

    -- 1. Índices secundarios, tal como están hoy, para recrearlos en la madre.
    SELECT array_agg(pg_get_indexdef(i.indexrelid)) INTO v_indices
      FROM pg_index i
     WHERE i.indrelid = 'public."{{tabla}}"'::regclass AND NOT i.indisprimary;

    -- 2. Apartar la previa (su PK cede el nombre a la nueva).
    ALTER TABLE public."{{tabla}}" RENAME TO "{{tabla}}_previa";
    ALTER TABLE public."{{tabla}}_previa" RENAME CONSTRAINT "PK_{{tabla}}" TO "PK_{{tabla}}_previa";

    -- 3. Madre particionada: mismas columnas, defaults y CHECK; la PK incluye la
    --    columna de partición porque PostgreSQL lo exige.
    CREATE TABLE public."{{tabla}}" (LIKE public."{{tabla}}_previa" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING COMMENTS)
        PARTITION BY RANGE ("{{columna}}");
    ALTER TABLE public."{{tabla}}" ADD CONSTRAINT "PK_{{tabla}}" PRIMARY KEY ("Id", "{{columna}}");

    -- Privilegios de los roles de aplicación: exactamente los de la previa, no
    -- los que ALTER DEFAULT PRIVILEGES acaba de dar a la tabla nueva.
    REVOKE ALL ON public."{{tabla}}" FROM PUBLIC, {{RolesAplicacion}};
    FOR r IN
        SELECT rol, privilegio
          FROM unnest(ARRAY['cae_app_runtime', 'cae_app_soporte', 'cae_app_aprovisionamiento']) AS rol,
               unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilegio
         WHERE has_table_privilege(rol, 'public."{{tabla}}_previa"', privilegio)
    LOOP
        EXECUTE format('GRANT %s ON public.%I TO %I', r.privilegio, '{{tabla}}', r.rol);
    END LOOP;

    PERFORM public.app_rls_copiar_politicas('public."{{tabla}}_previa"'::regclass, 'public."{{tabla}}"'::regclass);
    ALTER TABLE public."{{tabla}}" ENABLE ROW LEVEL SECURITY;
    ALTER TABLE public."{{tabla}}" FORCE ROW LEVEL SECURITY;

    -- 4. Particiones: del mes del evento más antiguo (acotado a diez años) al
    --    actual más {{MesesPorDelante}}, y la de por defecto.
    SELECT min("{{columna}}") INTO v_min FROM public."{{tabla}}_previa";
    v_mes := date_trunc('month', greatest(coalesce(v_min, now()), now() - interval '120 months') AT TIME ZONE 'UTC')::date;
    v_fin := (date_trunc('month', now() AT TIME ZONE 'UTC') + interval '{{MesesPorDelante}} months')::date;
    WHILE v_mes <= v_fin LOOP
        PERFORM public.app_particion_eventos_asegurar('public."{{tabla}}"'::regclass, v_mes);
        v_mes := (v_mes + interval '1 month')::date;
    END LOOP;
    CREATE TABLE public."{{tabla}}{{SufijoDefecto}}" PARTITION OF public."{{tabla}}" DEFAULT;
    PERFORM public.app_particion_eventos_proteger('public."{{tabla}}{{SufijoDefecto}}"'::regclass, 'public."{{tabla}}"'::regclass);

    -- 5. Copia y 6. comprobación: mismo recuento y misma suma de hashes por
    --    fila (la representación de texto de la fila incluye todas las columnas
    --    en el mismo orden, porque la madre se creó con LIKE).
    INSERT INTO public."{{tabla}}" SELECT * FROM public."{{tabla}}_previa";

    SELECT count(*), coalesce(sum(('x' || left(md5(t::text), 15))::bit(60)::bigint), 0)
      INTO v_filas_previa, v_hash_previa
      FROM public."{{tabla}}_previa" t;
    SELECT count(*), coalesce(sum(('x' || left(md5(t::text), 15))::bit(60)::bigint), 0)
      INTO v_filas_nueva, v_hash_nueva
      FROM public."{{tabla}}" t;
    IF v_filas_previa <> v_filas_nueva OR v_hash_previa <> v_hash_nueva THEN
        RAISE EXCEPTION 'P1-M2: la copia de "{{tabla}}" no cuadra: % filas antes, % después (hash % / %)',
            v_filas_previa, v_filas_nueva, v_hash_previa, v_hash_nueva;
    END IF;

    -- 7. Retirar la previa y 8. recrear los índices en la madre (cada
    --    partición los hereda, también las futuras).
    DROP TABLE public."{{tabla}}_previa";
    FOREACH v_def IN ARRAY coalesce(v_indices, '{}'::text[]) LOOP
        EXECUTE v_def;
    END LOOP;

    ANALYZE public."{{tabla}}";
END
$particionado$;
""";

    /// <summary>
    /// Deshace <see cref="ParticionarSql"/>: vuelve a una tabla ordinaria con PK
    /// <c>(Id)</c>, los mismos índices, privilegios y políticas, y la misma
    /// comprobación de recuento y hash antes de borrar la particionada.
    /// </summary>
    public static string DesparticionarSql(string tabla, string columna) => $$"""
DO $desparticionado$
DECLARE
    v_indices text[];
    v_def text;
    v_filas_previa bigint;
    v_hash_previa numeric;
    v_filas_nueva bigint;
    v_hash_nueva numeric;
    r record;
BEGIN
{{GuardaEvitaRls}}
    IF (SELECT relkind FROM pg_class WHERE oid = 'public."{{tabla}}"'::regclass) <> 'p' THEN
        RAISE EXCEPTION 'P1-M2: "{{tabla}}" no es una tabla particionada';
    END IF;

    SELECT array_agg(replace(pg_get_indexdef(i.indexrelid), ' ON ONLY ', ' ON ')) INTO v_indices
      FROM pg_index i
     WHERE i.indrelid = 'public."{{tabla}}"'::regclass AND NOT i.indisprimary;

    ALTER TABLE public."{{tabla}}" RENAME TO "{{tabla}}_particionada";
    ALTER TABLE public."{{tabla}}_particionada" RENAME CONSTRAINT "PK_{{tabla}}" TO "PK_{{tabla}}_particionada";

    CREATE TABLE public."{{tabla}}" (LIKE public."{{tabla}}_particionada" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING COMMENTS);
    ALTER TABLE public."{{tabla}}" ADD CONSTRAINT "PK_{{tabla}}" PRIMARY KEY ("Id");

    REVOKE ALL ON public."{{tabla}}" FROM PUBLIC, {{RolesAplicacion}};
    FOR r IN
        SELECT rol, privilegio
          FROM unnest(ARRAY['cae_app_runtime', 'cae_app_soporte', 'cae_app_aprovisionamiento']) AS rol,
               unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) AS privilegio
         WHERE has_table_privilege(rol, 'public."{{tabla}}_particionada"', privilegio)
    LOOP
        EXECUTE format('GRANT %s ON public.%I TO %I', r.privilegio, '{{tabla}}', r.rol);
    END LOOP;

    PERFORM public.app_rls_copiar_politicas('public."{{tabla}}_particionada"'::regclass, 'public."{{tabla}}"'::regclass);
    ALTER TABLE public."{{tabla}}" ENABLE ROW LEVEL SECURITY;
    ALTER TABLE public."{{tabla}}" FORCE ROW LEVEL SECURITY;

    INSERT INTO public."{{tabla}}" SELECT * FROM public."{{tabla}}_particionada";

    SELECT count(*), coalesce(sum(('x' || left(md5(t::text), 15))::bit(60)::bigint), 0)
      INTO v_filas_previa, v_hash_previa
      FROM public."{{tabla}}_particionada" t;
    SELECT count(*), coalesce(sum(('x' || left(md5(t::text), 15))::bit(60)::bigint), 0)
      INTO v_filas_nueva, v_hash_nueva
      FROM public."{{tabla}}" t;
    IF v_filas_previa <> v_filas_nueva OR v_hash_previa <> v_hash_nueva THEN
        RAISE EXCEPTION 'P1-M2: la copia de "{{tabla}}" no cuadra: % filas antes, % después', v_filas_previa, v_filas_nueva;
    END IF;

    DROP TABLE public."{{tabla}}_particionada";
    FOREACH v_def IN ARRAY coalesce(v_indices, '{}'::text[]) LOOP
        EXECUTE v_def;
    END LOOP;

    ANALYZE public."{{tabla}}";
END
$desparticionado$;
""";
}
