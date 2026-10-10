using Npgsql;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Huella de una base de datos leída de los catálogos de PostgreSQL: todo lo que
/// distingue a una base migrada de otra, expresado con nombres y definiciones en
/// texto, nunca con OID (que cambian de una base a otra aunque el contenido sea
/// el mismo).
///
/// <para>
/// Existe para comparar una base migrada de verdad con un clon de la plantilla
/// (<see cref="PlantillaMigradaDePruebas"/>). Cubre el esquema (relaciones,
/// columnas, índices, restricciones, secuencias, funciones, disparadores, tipos,
/// extensiones), la seguridad (texto completo de cada política RLS, los
/// indicadores <c>relrowsecurity</c> y <c>relforcerowsecurity</c> de cada tabla,
/// propietarios, permisos por tabla y por rol, privilegios por defecto,
/// dependencias de rol), los datos que dejan las migraciones y el nivel de base,
/// que es justo lo que <c>CREATE DATABASE … TEMPLATE</c> no copia: propietario,
/// permisos sobre la base y ajustes por base y por rol
/// (<c>pg_db_role_setting</c>).
/// </para>
///
/// <para>
/// La última categoría, el recuento de filas de cada catálogo no compartido, es
/// la red de seguridad de las anteriores: una clase de objeto que ninguna
/// consulta de aquí nombre cambia igualmente algún recuento.
/// </para>
/// </summary>
internal static class HuellaDeBaseDeDatos
{
    private const string EsquemasPropios =
        "n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname !~ '^pg_(toast|temp)'";

    private const string BaseActual = "(SELECT oid FROM pg_database WHERE datname = current_database())";

    /// <summary>Cada consulta devuelve dos columnas de texto: identidad y valor.</summary>
    private static readonly (string Categoria, string Sql)[] Consultas =
    [
        ("esquema", """
            SELECT n.nspname::text,
                   jsonb_build_object(
                       'propietario', pg_get_userbyid(n.nspowner),
                       'acl', (SELECT array_agg(a::text ORDER BY a::text) FROM unnest(n.nspacl) a))::text
            FROM pg_namespace n
            WHERE {ESQUEMAS}
            """),

        ("relación", """
            SELECT format('%I.%I', n.nspname, c.relname),
                   (to_jsonb(c) - ARRAY['oid', 'relname', 'relnamespace', 'reltype', 'reloftype', 'relowner', 'relam',
                                        'relfilenode', 'reltablespace', 'relpages', 'reltuples', 'relallvisible',
                                        'relallfrozen', 'reltoastrelid', 'relfrozenxid', 'relminmxid', 'relacl',
                                        'relpartbound', 'relrewrite']
                    || jsonb_build_object(
                       'propietario', pg_get_userbyid(c.relowner),
                       'metodo_de_acceso', (SELECT amname FROM pg_am WHERE oid = c.relam),
                       'tablespace', (SELECT spcname FROM pg_tablespace WHERE oid = c.reltablespace),
                       'tiene_toast', c.reltoastrelid <> 0,
                       'acl', (SELECT array_agg(a::text ORDER BY a::text) FROM unnest(c.relacl) a),
                       'limite_de_particion', pg_get_expr(c.relpartbound, c.oid),
                       'clave_de_particion', CASE WHEN c.relkind = 'p' THEN pg_get_partkeydef(c.oid) END,
                       'hereda_de', (SELECT array_agg(i.inhparent::regclass::text ORDER BY i.inhseqno)
                                     FROM pg_inherits i WHERE i.inhrelid = c.oid),
                       'vista', CASE WHEN c.relkind IN ('v', 'm') THEN pg_get_viewdef(c.oid, true) END))::text
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {ESQUEMAS}
            """),

        ("RLS de la tabla", """
            SELECT format('%I.%I', n.nspname, c.relname),
                   format('habilitada=%s forzada=%s', c.relrowsecurity, c.relforcerowsecurity)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p') AND {ESQUEMAS}
            """),

        ("política RLS", """
            SELECT format('%I.%I · %I', p.schemaname, p.tablename, p.policyname),
                   (to_jsonb(p) - ARRAY['schemaname', 'tablename', 'policyname'])::text
            FROM pg_policies p
            """),

        ("permiso sobre la tabla", """
            SELECT format('%I.%I → %s', n.nspname, c.relname,
                          CASE WHEN x.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(x.grantee) END),
                   string_agg(
                       x.privilege_type || CASE WHEN x.is_grantable THEN ' (con GRANT OPTION)' ELSE '' END
                           || ' otorgado por ' || pg_get_userbyid(x.grantor),
                       ', ' ORDER BY x.privilege_type, pg_get_userbyid(x.grantor))
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace, LATERAL aclexplode(c.relacl) x
            WHERE {ESQUEMAS}
            GROUP BY 1
            """),

        ("privilegios por defecto", """
            SELECT format('%s · %s · %s', pg_get_userbyid(d.defaclrole),
                          coalesce((SELECT nspname::text FROM pg_namespace WHERE oid = d.defaclnamespace),
                                   '(todos los esquemas)'),
                          d.defaclobjtype),
                   (SELECT array_agg(x::text ORDER BY x::text) FROM unnest(d.defaclacl) x)::text
            FROM pg_default_acl d
            """),

        ("columna", """
            SELECT format('%I.%I #%s', n.nspname, c.relname, a.attnum),
                   (to_jsonb(a) - ARRAY['attrelid', 'atttypid', 'attcollation', 'attacl', 'attmissingval', 'attcacheoff']
                    || jsonb_build_object(
                       'tipo', format_type(a.atttypid, a.atttypmod),
                       'intercalacion', (SELECT collname FROM pg_collation WHERE oid = a.attcollation),
                       'por_defecto', (SELECT pg_get_expr(d.adbin, d.adrelid) FROM pg_attrdef d
                                       WHERE d.adrelid = a.attrelid AND d.adnum = a.attnum),
                       'valor_ausente', a.attmissingval::text,
                       'acl', (SELECT array_agg(x::text ORDER BY x::text) FROM unnest(a.attacl) x)))::text
            FROM pg_attribute a
                 JOIN pg_class c ON c.oid = a.attrelid
                 JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE a.attnum > 0 AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'c') AND {ESQUEMAS}
            """),

        ("índice", """
            SELECT format('%I.%I', n.nspname, ci.relname),
                   (to_jsonb(i) - ARRAY['indexrelid', 'indrelid', 'indcollation', 'indclass', 'indexprs', 'indpred']
                    || jsonb_build_object(
                       'tabla', i.indrelid::regclass::text,
                       'definicion', pg_get_indexdef(i.indexrelid)))::text
            FROM pg_index i
                 JOIN pg_class ci ON ci.oid = i.indexrelid
                 JOIN pg_namespace n ON n.oid = ci.relnamespace
            WHERE {ESQUEMAS}
            """),

        ("restricción", """
            SELECT format('%s · %I',
                          coalesce(nullif(k.conrelid, 0::oid)::regclass::text, nullif(k.contypid, 0::oid)::regtype::text),
                          k.conname),
                   (to_jsonb(k) - ARRAY['oid', 'conname', 'connamespace', 'conrelid', 'contypid', 'conindid',
                                        'conparentid', 'confrelid', 'conpfeqop', 'conppeqop', 'conffeqop',
                                        'conexclop', 'conbin']
                    || jsonb_build_object(
                       'definicion', pg_get_constraintdef(k.oid, true),
                       'referencia', nullif(k.confrelid, 0::oid)::regclass::text,
                       'indice', nullif(k.conindid, 0::oid)::regclass::text,
                       'madre', (SELECT m.conrelid::regclass::text || ' · ' || m.conname
                                 FROM pg_constraint m WHERE m.oid = k.conparentid)))::text
            FROM pg_constraint k JOIN pg_namespace n ON n.oid = k.connamespace
            WHERE {ESQUEMAS}
            """),

        ("secuencia", """
            SELECT format('%I.%I', s.schemaname, s.sequencename),
                   (to_jsonb(s) - ARRAY['schemaname', 'sequencename']
                    || jsonb_build_object(
                       'pertenece_a', (SELECT d.refobjid::regclass::text || ' #' || d.refobjsubid
                                       FROM pg_depend d
                                       WHERE d.classid = 'pg_class'::regclass
                                         AND d.objid = format('%I.%I', s.schemaname, s.sequencename)::regclass
                                         AND d.refclassid = 'pg_class'::regclass
                                         AND d.deptype IN ('a', 'i')
                                       LIMIT 1)))::text
            FROM pg_sequences s
            """),

        ("función", """
            SELECT p.oid::regprocedure::text,
                   (to_jsonb(p) - ARRAY['oid', 'proname', 'pronamespace', 'proowner', 'prolang', 'provariadic',
                                        'prosupport', 'prorettype', 'proargtypes', 'proallargtypes',
                                        'proargdefaults', 'prosqlbody', 'proacl', 'protrftypes']
                    || jsonb_build_object(
                       'esquema', n.nspname,
                       'propietario', pg_get_userbyid(p.proowner),
                       'lenguaje', (SELECT lanname FROM pg_language WHERE oid = p.prolang),
                       'devuelve', pg_get_function_result(p.oid),
                       'argumentos', pg_get_function_arguments(p.oid),
                       'definicion', CASE WHEN p.prokind <> 'a' THEN pg_get_functiondef(p.oid) END,
                       'acl', (SELECT array_agg(x::text ORDER BY x::text) FROM unnest(p.proacl) x)))::text
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE {ESQUEMAS}
            """),

        ("disparador", """
            SELECT format('%s · %I', t.tgrelid::regclass, t.tgname),
                   jsonb_build_object(
                       'definicion', pg_get_triggerdef(t.oid, true),
                       'habilitado', t.tgenabled,
                       'funcion', t.tgfoid::regprocedure::text,
                       'tipo', t.tgtype,
                       'heredado', t.tgparentid <> 0)::text
            FROM pg_trigger t
                 JOIN pg_class c ON c.oid = t.tgrelid
                 JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT t.tgisinternal AND {ESQUEMAS}
            """),

        // Los disparadores internos de las claves ajenas llevan un OID en el
        // nombre: se identifican por tabla, restricción, función y tipo.
        ("disparador interno", """
            SELECT format('%s · %s · %s · tipo %s', t.tgrelid::regclass,
                          (SELECT k.conname FROM pg_constraint k WHERE k.oid = t.tgconstraint),
                          t.tgfoid::regproc, t.tgtype),
                   string_agg(format('habilitado=%s diferible=%s diferido=%s',
                                     t.tgenabled, t.tgdeferrable, t.tginitdeferred), '; ')
            FROM pg_trigger t
                 JOIN pg_class c ON c.oid = t.tgrelid
                 JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE t.tgisinternal AND {ESQUEMAS}
            GROUP BY 1
            """),

        ("tipo", """
            SELECT format('%I.%I', n.nspname, t.typname),
                   jsonb_build_object(
                       'clase', t.typtype,
                       'propietario', pg_get_userbyid(t.typowner),
                       'acl', (SELECT array_agg(x::text ORDER BY x::text) FROM unnest(t.typacl) x),
                       'etiquetas', (SELECT array_agg(e.enumlabel ORDER BY e.enumsortorder)
                                     FROM pg_enum e WHERE e.enumtypid = t.oid),
                       'base', CASE WHEN t.typtype = 'd' THEN format_type(t.typbasetype, t.typtypmod) END,
                       'no_nulo', t.typnotnull,
                       'por_defecto', t.typdefault)::text
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE {ESQUEMAS}
            """),

        ("extensión", """
            SELECT e.extname::text,
                   jsonb_build_object(
                       'version', e.extversion,
                       'esquema', (SELECT nspname FROM pg_namespace WHERE oid = e.extnamespace),
                       'propietario', pg_get_userbyid(e.extowner),
                       'reubicable', e.extrelocatable)::text
            FROM pg_extension e
            """),

        ("regla", """
            SELECT format('%I.%I · %I', r.schemaname, r.tablename, r.rulename), r.definition
            FROM pg_rules r
            """),

        ("disparador de evento", """
            SELECT e.evtname::text,
                   jsonb_build_object(
                       'evento', e.evtevent, 'propietario', pg_get_userbyid(e.evtowner),
                       'funcion', e.evtfoid::regprocedure::text, 'habilitado', e.evtenabled, 'etiquetas', e.evttags)::text
            FROM pg_event_trigger e
            """),

        ("publicación", """
            SELECT p.pubname::text,
                   (to_jsonb(p) - ARRAY['oid', 'pubname', 'pubowner']
                    || jsonb_build_object(
                       'propietario', pg_get_userbyid(p.pubowner),
                       'tablas', (SELECT array_agg(format('%I.%I', t.schemaname, t.tablename) ORDER BY 1)
                                  FROM pg_publication_tables t WHERE t.pubname = p.pubname)))::text
            FROM pg_publication p
            """),

        ("estadística extendida", """
            SELECT format('%I.%I', n.nspname, s.stxname), pg_get_statisticsobjdef(s.oid)
            FROM pg_statistic_ext s JOIN pg_namespace n ON n.oid = s.stxnamespace
            """),

        ("comentario", """
            SELECT i.type || ' ' || i.identity, d.description
            FROM pg_description d, LATERAL pg_identify_object(d.classoid, d.objoid, d.objsubid) i
            WHERE d.objoid >= 16384
            """),

        ("dependencia de rol", """
            SELECT format('%s → %s (%s)',
                          regexp_replace(i.type || ' ' || i.identity, 'pg_toast_\d+', 'pg_toast_N', 'g'),
                          pg_get_userbyid(s.refobjid), s.deptype),
                   count(*)::text
            FROM pg_shdepend s, LATERAL pg_identify_object(s.classid, s.objid, s.objsubid) i
            WHERE s.dbid = {BASE} AND s.refclassid = 'pg_authid'::regclass
            GROUP BY 1
            """),

        ("historial de migraciones", """
            SELECT h."MigrationId"::text, h."ProductVersion"::text FROM "__EFMigrationsHistory" h
            """),

        ("contenido de la tabla", """
            SELECT format('%I.%I', n.nspname, c.relname),
                   (xpath('/row/h/text()', query_to_xml(
                       format('SELECT count(*) || '' filas, md5 '' || md5(coalesce(string_agg(t::text, E''\n'' ORDER BY t::text), '''')) AS h FROM %I.%I t',
                              n.nspname, c.relname),
                       false, true, '')))[1]::text
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'r' AND {ESQUEMAS}
            """),

        // ---- Nivel de base: lo que CREATE DATABASE … TEMPLATE no copia. ----

        ("base de datos", """
            SELECT 'atributos',
                   (to_jsonb(d) - ARRAY['oid', 'datname', 'datdba', 'dattablespace', 'datfrozenxid', 'datminmxid', 'datacl']
                    || jsonb_build_object(
                       'propietario', pg_get_userbyid(d.datdba),
                       'tablespace', (SELECT spcname FROM pg_tablespace WHERE oid = d.dattablespace),
                       'acl', (SELECT array_agg(x::text ORDER BY x::text) FROM unnest(d.datacl) x)))::text
            FROM pg_database d
            WHERE d.datname = current_database()
            """),

        ("privilegio efectivo sobre la base", """
            SELECT format('%s · %s', r.rol, p.privilegio),
                   has_database_privilege(r.rol, current_database(), p.privilegio)::text
            FROM (SELECT rolname::text AS rol FROM pg_roles WHERE rolname LIKE 'cae\_%'
                  UNION ALL SELECT 'public') r
                 CROSS JOIN unnest(ARRAY['CONNECT', 'CREATE', 'TEMPORARY']) AS p(privilegio)
            """),

        ("ajuste de base o de rol en la base", """
            SELECT coalesce((SELECT rolname::text FROM pg_roles WHERE oid = s.setrole), '(todos los roles)'),
                   (SELECT array_agg(x ORDER BY x) FROM unnest(s.setconfig) x)::text
            FROM pg_db_role_setting s
            WHERE s.setdatabase = {BASE}
            """),

        ("comentario de la base", """
            SELECT 'comentario', d.description
            FROM pg_shdescription d
            WHERE d.classoid = 'pg_database'::regclass AND d.objoid = {BASE}
            """),

        ("etiqueta de seguridad de la base", """
            SELECT l.provider, l.label
            FROM pg_shseclabel l
            WHERE l.classoid = 'pg_database'::regclass AND l.objoid = {BASE}
            """),

        // ---- Red de seguridad: cuántas filas tiene cada catálogo de la base. ----
        // Fuera quedan los compartidos por el clúster (otras bases los mueven) y
        // las estadísticas del planificador, que dependen de cuándo pasó ANALYZE.

        ("recuento del catálogo", """
            SELECT c.relname::text,
                   (xpath('/row/n/text()', query_to_xml(
                       format('SELECT count(*) AS n FROM pg_catalog.%I', c.relname), false, true, '')))[1]::text
            FROM pg_class c
            WHERE c.relnamespace = 'pg_catalog'::regnamespace AND c.relkind = 'r' AND NOT c.relisshared
              AND c.relname NOT IN ('pg_statistic', 'pg_statistic_ext_data')
            """),
    ];

    /// <summary>Las categorías que la huella sabe leer. Para el control de que ninguna queda vacía.</summary>
    internal static IReadOnlyList<string> Categorias { get; } = Consultas.Select(c => c.Categoria).ToArray();

    /// <summary>
    /// Lee la huella de la base a la que apunta la cadena, que debe autenticar
    /// como su propietario: un rol sujeto a RLS no vería las filas.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, string>> CalcularAsync(string cadenaDelPropietario)
    {
        var huella = new SortedDictionary<string, string>(StringComparer.Ordinal);

        await using var conexion = new NpgsqlConnection(cadenaDelPropietario);
        await conexion.OpenAsync();

        foreach (var (categoria, sql) in Consultas)
        {
            var consulta = sql
                .Replace("{ESQUEMAS}", EsquemasPropios, StringComparison.Ordinal)
                .Replace("{BASE}", BaseActual, StringComparison.Ordinal);

            await using var comando = new NpgsqlCommand(consulta, conexion) { CommandTimeout = 120 };
            await using var lector = await comando.ExecuteReaderAsync();
            while (await lector.ReadAsync())
            {
                var clave = $"[{categoria}] {(lector.IsDBNull(0) ? "(nulo)" : lector.GetString(0))}";
                var valor = lector.IsDBNull(1) ? "(nulo)" : lector.GetString(1);

                // Una identidad repetida no se pierde ni rompe la lectura: se acumula.
                huella[clave] = huella.TryGetValue(clave, out var previo)
                    ? string.Join(" ‖ ", new[] { previo, valor }.OrderBy(v => v, StringComparer.Ordinal))
                    : valor;
            }
        }

        return huella;
    }

    /// <summary>
    /// Diferencias entre dos huellas, una línea por entrada que falta, sobra o
    /// cambia, nombrando la categoría y el objeto.
    /// </summary>
    internal static IReadOnlyList<string> Diferencias(
        IReadOnlyDictionary<string, string> primera, string nombreDeLaPrimera,
        IReadOnlyDictionary<string, string> segunda, string nombreDeLaSegunda)
    {
        var diferencias = new List<string>();

        foreach (var clave in primera.Keys.Union(segunda.Keys).OrderBy(c => c, StringComparer.Ordinal))
        {
            var enPrimera = primera.TryGetValue(clave, out var valorPrimera);
            var enSegunda = segunda.TryGetValue(clave, out var valorSegunda);

            if (enPrimera && !enSegunda)
                diferencias.Add($"{clave}: solo en {nombreDeLaPrimera} = {Recortar(valorPrimera!)}");
            else if (!enPrimera && enSegunda)
                diferencias.Add($"{clave}: solo en {nombreDeLaSegunda} = {Recortar(valorSegunda!)}");
            else if (!string.Equals(valorPrimera, valorSegunda, StringComparison.Ordinal))
                diferencias.Add(
                    $"{clave}: {nombreDeLaPrimera} = {Recortar(valorPrimera!)} ≠ {nombreDeLaSegunda} = {Recortar(valorSegunda!)}");
        }

        return diferencias;
    }

    private static string Recortar(string valor) => valor.Length <= 400 ? valor : valor[..400] + "…";
}
