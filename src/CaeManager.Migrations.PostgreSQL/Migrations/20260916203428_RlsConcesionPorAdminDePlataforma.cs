using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// PD-A3: abre, de forma acotada y deliberada, el <c>WITH CHECK</c> del
    /// plano de privilegio de plataforma
    /// (20260821150319_RlsPlanoPrivilegioPlataforma) para admitir la primera
    /// vía de concesión a un TERCERO — hasta ahora imposible por diseño: esa
    /// migración solo admitía filas que nombraran al propio usuario de la
    /// sesión, y su propio comentario decía explícitamente que introducir la
    /// capacidad de conceder a terceros era decisión de la fase que
    /// construyera la apertura de sesiones. Es esta.
    ///
    /// <b>Qué NO cambia</b>: la rama de autoconcesión
    /// (<c>UsuarioPlataformaId = app.usuario_id</c>) sigue exactamente igual
    /// en las tres tablas. Esta migración solo AÑADE una segunda rama.
    ///
    /// <b>Dos funciones <c>SECURITY DEFINER</c>, primeras del repositorio.</b>
    /// Ambas evalúan <c>ConcesionesPrivilegio</c> desde dentro de la política
    /// de esa misma tabla — sin <c>SECURITY DEFINER</c> el <c>SELECT</c>
    /// recursaría sobre la propia política que lo invoca (el USING de la
    /// función correría con los privilegios de quien la ejecuta, que ya está
    /// dentro de la evaluación del WITH CHECK). <c>search_path</c> se fija
    /// explícitamente a <c>pg_catalog, pg_temp</c> — SIN <c>public</c> — y las
    /// dos únicas relaciones que tocan van cualificadas <c>public.</c> en el
    /// cuerpo: omitir <c>pg_temp</c> del <c>search_path</c> no lo excluye, lo
    /// pone el PRIMERO (antes que <c>pg_catalog</c>), así que cualquier
    /// llamador con <c>TEMP</c> sobre la base (PUBLIC la tiene por defecto)
    /// podría crear <c>"ConcesionesPrivilegio"</c> temporal y hacer que la
    /// función la lea, autoconcediéndose <c>AdminPlataforma</c>. Doble
    /// cinturón: <c>pg_temp</c> fijado al final Y las relaciones cualificadas,
    /// así que el orden de búsqueda deja de decidir nada aunque alguien edite
    /// la línea. <c>EXECUTE</c> se revoca de <c>PUBLIC</c> en las dos y solo
    /// se concede a <c>cae_app_runtime</c> — nunca a
    /// <c>cae_app_aprovisionamiento</c>: conceder un privilegio corre bajo la
    /// conexión ordinaria del admin, nunca bajo el rol de aprovisionamiento.
    ///
    /// <b>Por qué dos tablas y no solo la concesión.</b>
    /// <c>ConcesionPrivilegio.SobreTenants</c> crea SIEMPRE padre
    /// (<c>ConcesionesPrivilegio</c>) + una o más hijas
    /// (<c>TenantsAlcanzadosPorConcesion</c>). Ampliar solo la política del
    /// padre habría dejado la hija bloqueada por su propia política —que
    /// hereda el mismo predicado de autoconcesión— y el INSERT del agregado
    /// completo habría fallado a mitad, siempre. La comprobación de cobertura
    /// real por tenant vive en la HIJA (<c>app_es_admin_plataforma_sobre</c>,
    /// con el tenant como parámetro) porque es la única fila que lleva
    /// <c>TenantId</c>; el padre solo exige que el concedente tenga
    /// <c>AdminPlataforma</c> vigente en ALGÚN tenant
    /// (<c>app_es_admin_plataforma</c>, sin parámetro de tenant) — más laxo a
    /// propósito, porque la prueba fuerte la aporta la hija en la MISMA
    /// transacción.
    ///
    /// <b>Tres restricciones en la rama de admin del padre, ninguna
    /// opcional</b>: <c>ConcedidaPorUsuarioId = app.usuario_id</c> (quien
    /// concede se declara), <c>EsAlcanceGlobal = false</c> (imprescindible:
    /// sin ella un admin podría acuñar una concesión de alcance GLOBAL para
    /// un beneficiario y saltarse por completo la comprobación de cobertura
    /// de la hija, que solo se ejercita cuando hay filas de alcance) y
    /// <c>Capacidad = 'Aprovisionamiento'</c> (posible porque la columna se
    /// persiste como texto, <c>ConcesionPrivilegioConfiguration</c> —
    /// <c>HasConversion&lt;string&gt;().HasMaxLength(30)</c> — así que el
    /// ordinal del enum no interviene). Un admin NO puede usar esta vía para
    /// concederse a sí mismo ni a otro <c>AdminPlataforma</c> ni
    /// <c>BreakGlass</c>: solo <c>Aprovisionamiento</c>, solo acotada, solo a
    /// un tercero.
    ///
    /// Pura DDL de servidor: no cambia el modelo de EF, así que no hay diffs
    /// que aplicar en el snapshot.
    ///
    /// <b>Comparación de vigencia contra <c>now()</c> a secas, nunca
    /// <c>now() AT TIME ZONE 'utc'</c></b>: `VigenciaDesde`/`VigenciaHasta`
    /// son `timestamp with time zone` (confirmado en el snapshot de EF); una
    /// primera versión de esta migración comparaba contra
    /// `now() AT TIME ZONE 'utc'` (`timestamp` sin zona) y Postgres, al
    /// castear ese lado de vuelta a `timestamptz` para poder comparar,
    /// reinterpreta la hora usando el TIMEZONE DE LA SESIÓN — no UTC—, así
    /// que con cualquier sesión que no esté en UTC la comparación queda
    /// desplazada por el offset (demostrado con datos reales: fila Vigente
    /// con Desde 10 minutos en el pasado, y la función devolvía false).
    /// `timestamptz <= timestamptz` no tiene esa ambigüedad.
    ///
    /// <b>El USING del padre también se amplía, no solo el WITH CHECK.</b> La
    /// hija (<c>TenantsAlcanzadosPorConcesion</c>) valida su propio WITH CHECK
    /// con un <c>EXISTS(SELECT 1 FROM "ConcesionesPrivilegio" ...)</c> — esa
    /// subconsulta corre bajo el rol restringido del llamador (NO es
    /// <c>SECURITY DEFINER</c>, es SQL de política inline) y por tanto está
    /// sujeta al USING del padre. Con el USING original (solo
    /// <c>UsuarioPlataformaId = app.usuario_id</c>) el concedente no podía VER
    /// la fila padre que él mismo acababa de insertar —esa fila nombra al
    /// BENEFICIARIO, no a él—, así que el EXISTS de la hija no la encontraba y
    /// el INSERT de la hija fallaba con 42501 aunque la función de autoridad
    /// devolviera <c>true</c>. Diagnosticado con una llamada de depuración a
    /// la función SIN restricción de rol (devolvía <c>true</c>, contradiciendo
    /// el fallo real) — la causa no estaba en la función, estaba en qué filas
    /// del padre son VISIBLES bajo RLS al insertar la hija. Efecto secundario
    /// deseado, alineado con "el concedente puede leer y revocar lo que
    /// concedió": el USING ahora también admite
    /// <c>ConcedidaPorUsuarioId = app.usuario_id</c>.
    /// </summary>
    public partial class RlsConcesionPorAdminDePlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE FUNCTION app_es_admin_plataforma(usuario uuid) RETURNS boolean
  LANGUAGE sql STABLE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
  SELECT EXISTS (SELECT 1 FROM public.""ConcesionesPrivilegio"" c
    WHERE c.""UsuarioPlataformaId"" = usuario AND c.""Capacidad"" = 'AdminPlataforma'
      AND c.""Estado"" = 'Vigente' AND c.""VigenciaDesde"" <= now()
      AND (c.""VigenciaHasta"" IS NULL OR now() < c.""VigenciaHasta""));
$$;
REVOKE ALL ON FUNCTION app_es_admin_plataforma(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app_es_admin_plataforma(uuid) TO cae_app_runtime;

CREATE FUNCTION app_es_admin_plataforma_sobre(usuario uuid, tenant uuid) RETURNS boolean
  LANGUAGE sql STABLE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
  SELECT EXISTS (SELECT 1 FROM public.""ConcesionesPrivilegio"" c
    WHERE c.""UsuarioPlataformaId"" = usuario AND c.""Capacidad"" = 'AdminPlataforma'
      AND c.""Estado"" = 'Vigente' AND c.""VigenciaDesde"" <= now()
      AND (c.""VigenciaHasta"" IS NULL OR now() < c.""VigenciaHasta"")
      AND (c.""EsAlcanceGlobal"" OR EXISTS (SELECT 1 FROM public.""TenantsAlcanzadosPorConcesion"" t
           WHERE t.""ConcesionPrivilegioId"" = c.""Id"" AND t.""TenantId"" = tenant)));
$$;
REVOKE ALL ON FUNCTION app_es_admin_plataforma_sobre(uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app_es_admin_plataforma_sobre(uuid, uuid) TO cae_app_runtime;

DROP POLICY IF EXISTS privilegio_del_usuario ON ""ConcesionesPrivilegio"";
CREATE POLICY privilegio_del_usuario ON ""ConcesionesPrivilegio""
    USING (
        ""UsuarioPlataformaId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid
        OR ""ConcedidaPorUsuarioId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid
    )
    WITH CHECK (
        ""UsuarioPlataformaId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid
        OR (
            ""ConcedidaPorUsuarioId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid
            AND ""EsAlcanceGlobal"" = false
            AND ""Capacidad"" = 'Aprovisionamiento'
            AND app_es_admin_plataforma(NULLIF(current_setting('app.usuario_id', true), '')::uuid)
        )
    );

DROP POLICY IF EXISTS privilegio_del_usuario ON ""TenantsAlcanzadosPorConcesion"";
CREATE POLICY privilegio_del_usuario ON ""TenantsAlcanzadosPorConcesion""
    USING (EXISTS (
        SELECT 1 FROM ""ConcesionesPrivilegio"" c
        WHERE c.""Id"" = ""ConcesionPrivilegioId""
          AND c.""UsuarioPlataformaId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid))
    WITH CHECK (EXISTS (
        SELECT 1 FROM ""ConcesionesPrivilegio"" c
        WHERE c.""Id"" = ""ConcesionPrivilegioId""
          AND (c.""UsuarioPlataformaId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid
            OR (c.""ConcedidaPorUsuarioId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid
                AND app_es_admin_plataforma_sobre(
                      NULLIF(current_setting('app.usuario_id', true), '')::uuid, ""TenantId"")))));
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS privilegio_del_usuario ON ""TenantsAlcanzadosPorConcesion"";
CREATE POLICY privilegio_del_usuario ON ""TenantsAlcanzadosPorConcesion""
    USING (EXISTS (
        SELECT 1 FROM ""ConcesionesPrivilegio"" c
        WHERE c.""Id"" = ""ConcesionPrivilegioId""
          AND c.""UsuarioPlataformaId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid))
    WITH CHECK (EXISTS (
        SELECT 1 FROM ""ConcesionesPrivilegio"" c
        WHERE c.""Id"" = ""ConcesionPrivilegioId""
          AND c.""UsuarioPlataformaId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid));

DROP POLICY IF EXISTS privilegio_del_usuario ON ""ConcesionesPrivilegio"";
CREATE POLICY privilegio_del_usuario ON ""ConcesionesPrivilegio""
    USING (""UsuarioPlataformaId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid)
    WITH CHECK (""UsuarioPlataformaId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid);

DROP FUNCTION IF EXISTS app_es_admin_plataforma_sobre(uuid, uuid);
DROP FUNCTION IF EXISTS app_es_admin_plataforma(uuid);
");
        }
    }
}
