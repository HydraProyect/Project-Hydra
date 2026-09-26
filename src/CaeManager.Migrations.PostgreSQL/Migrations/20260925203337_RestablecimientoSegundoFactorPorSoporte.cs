using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// ADR-011 § 8.7, punto 3: Soporte TALVEG restablece la verificación en dos pasos del
    /// Administrador único de un Tenant desde una Sesión Privilegiada con la capacidad
    /// <c>RestablecimientoSegundoFactor</c>.
    ///
    /// <para>
    /// <b>Por qué una función.</b> Una conexión con Sesión Privilegiada adopta
    /// <c>cae_app_soporte</c>, que solo tiene SELECT. Darle UPDATE sobre
    /// <c>AspNetUsers</c> le dejaría escribir cualquier cuenta del Tenant de contexto
    /// (la política <c>cuentas_modificacion</c> de 20260925184521_RlsAspNetUsers no
    /// distingue qué columna ni qué cuenta), y además los tokens. En vez de eso, el
    /// único acto de escritura que esa sesión
    /// puede hacer es esta función, y la función vuelve a comprobar en la base todo
    /// lo que Application ya comprobó: contexto RLS firmado, sesión abierta y sin
    /// simulación sobre el Tenant del contexto, concesión vigente por Tenant con esta
    /// capacidad y concedida por otra persona, y cuenta del Tenant que sea su único
    /// Administrador activo con la 2FA activa. Escribe el cambio y su auditoría en la
    /// misma transacción. Como SECURITY DEFINER de un propietario que evita la RLS,
    /// filtra ella misma la cuenta por el Tenant del contexto firmado. No se toca
    /// ninguna política de lectura ni se retira
    /// <c>FORCE</c>: el cruce es este contrato, de un solo acto.
    /// </para>
    ///
    /// <para>
    /// <b>Por qué se amplía <c>privilegio_del_usuario</c>.</b> Su WITH CHECK solo
    /// dejaba a un AdminPlataforma conceder <c>Aprovisionamiento</c> a otro usuario.
    /// La capacidad nueva se concede por el mismo camino y con las mismas
    /// condiciones (no global, concedente AdminPlataforma).
    /// </para>
    ///
    /// <para>
    /// <b>Devuelve un código, no lanza.</b> <c>restablecido</c> o el motivo de la
    /// negativa, para que Application lo traduzca sin adivinar por un SQLSTATE. Solo
    /// <c>cae_app_soporte</c> puede ejecutarla: cualquier otro rol recibe 42501.
    /// </para>
    /// </summary>
    public partial class RestablecimientoSegundoFactorPorSoporte : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
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
            AND ""Capacidad"" IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor')
            AND app_es_admin_plataforma(NULLIF(current_setting('app.usuario_id', true), '')::uuid)
        )
    );

CREATE FUNCTION app_restablecer_segundo_factor_por_soporte(p_sesion uuid, p_usuario uuid) RETURNS text
  LANGUAGE plpgsql VOLATILE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  v_actor uuid;
  v_tenant uuid;
  v_dos_factores boolean;
BEGIN
  IF NOT public.app_ctx_valido() THEN
    RETURN 'contexto_no_valido';
  END IF;
  v_actor := public.app_ctx_usuario_id();
  v_tenant := public.app_ctx_tenant_id();
  IF v_actor IS NULL OR v_tenant IS NULL OR p_sesion IS NULL OR p_usuario IS NULL THEN
    RETURN 'contexto_no_valido';
  END IF;

  -- La sesión: abierta, sin simular a nadie, sobre el Tenant del contexto, del
  -- actor del contexto, y amparada por una concesión vigente de esta capacidad,
  -- por Tenant, que alcanza ese Tenant y que concedió otra persona.
  IF NOT EXISTS (
      SELECT 1
      FROM public.""SesionesPrivilegiadas"" s
      JOIN public.""ConcesionesPrivilegio"" c ON c.""Id"" = s.""ConcesionPrivilegioId""
      WHERE s.""Id"" = p_sesion
        AND s.""TenantObjetivoId"" = v_tenant
        AND s.""UsuarioSimuladoId"" IS NULL
        AND s.""CerradaEnUtc"" IS NULL
        AND s.""InicioEnUtc"" <= now()
        AND s.""ExpiraEnUtc"" > now()
        AND c.""UsuarioPlataformaId"" = v_actor
        AND c.""Capacidad"" = 'RestablecimientoSegundoFactor'
        AND c.""Estado"" = 'Vigente'
        AND c.""EsAlcanceGlobal"" = false
        AND c.""VigenciaDesde"" <= now()
        AND (c.""VigenciaHasta"" IS NULL OR c.""VigenciaHasta"" > now())
        AND c.""ConcedidaPorUsuarioId"" IS NOT NULL
        AND c.""ConcedidaPorUsuarioId"" <> c.""UsuarioPlataformaId""
        AND EXISTS (
            SELECT 1 FROM public.""TenantsAlcanzadosPorConcesion"" t
            WHERE t.""ConcesionPrivilegioId"" = c.""Id"" AND t.""TenantId"" = v_tenant))
  THEN
    RETURN 'sesion_no_autorizada';
  END IF;

  -- Quien la ejerce es un Actor de Plataforma TALVEG: su cuenta es de un Tenant
  -- de plataforma. EsPlataforma aquí solo restringe, no concede nada.
  IF NOT EXISTS (
      SELECT 1 FROM public.""AspNetUsers"" a
      JOIN public.""Tenants"" tp ON tp.""Id"" = a.""TenantId""
      WHERE a.""Id"" = v_actor AND tp.""EsPlataforma"")
  THEN
    RETURN 'sesion_no_autorizada';
  END IF;

  -- La cuenta: del Tenant del contexto y no desactivada (mismo umbral que
  -- ApplicationUser.UmbralDeCuentaDesactivada). La fila queda bloqueada hasta el final.
  SELECT u.""TwoFactorEnabled"" INTO v_dos_factores
  FROM public.""AspNetUsers"" u
  WHERE u.""Id"" = p_usuario
    AND u.""TenantId"" = v_tenant
    AND (u.""LockoutEnd"" IS NULL OR u.""LockoutEnd"" <= now() + interval '365 days')
  FOR UPDATE;
  IF NOT FOUND THEN
    RETURN 'cuenta_no_encontrada';
  END IF;

  IF NOT EXISTS (
      SELECT 1 FROM public.""AspNetUserRoles"" ur
      JOIN public.""AspNetRoles"" r ON r.""Id"" = ur.""RoleId""
      WHERE ur.""UserId"" = p_usuario AND r.""NormalizedName"" = 'ADMINISTRADOR')
  THEN
    RETURN 'no_es_administrador';
  END IF;

  IF EXISTS (
      SELECT 1 FROM public.""AspNetUsers"" o
      JOIN public.""AspNetUserRoles"" ur ON ur.""UserId"" = o.""Id""
      JOIN public.""AspNetRoles"" r ON r.""Id"" = ur.""RoleId""
      WHERE o.""TenantId"" = v_tenant
        AND o.""Id"" <> p_usuario
        AND r.""NormalizedName"" = 'ADMINISTRADOR'
        AND (o.""LockoutEnd"" IS NULL OR o.""LockoutEnd"" <= now() + interval '365 days'))
  THEN
    RETURN 'hay_otro_administrador';
  END IF;

  IF NOT v_dos_factores THEN
    RETURN 'sin_segundo_factor';
  END IF;

  -- El acto: lo mismo que el camino del Administrador (P0-8). El sello nuevo
  -- cierra las sesiones abiertas de la cuenta en su siguiente validación.
  UPDATE public.""AspNetUsers""
  SET ""TwoFactorEnabled"" = false,
      ""SecurityStamp"" = upper(replace(gen_random_uuid()::text, '-', '')),
      ""ConcurrencyStamp"" = gen_random_uuid()::text
  WHERE ""Id"" = p_usuario;

  -- Auditoría con la forma de AuditoriaInterceptor (sello y valor de token
  -- enmascarados), más la vía y la sesión que ampara el acto. Actor real =
  -- técnico de Soporte TALVEG; la cuenta afectada va en EntidadId.
  INSERT INTO public.""RegistrosAuditoria""
    (""Id"", ""TenantId"", ""EntidadTipo"", ""EntidadId"", ""Accion"", ""DatosAntes"", ""DatosDespues"",
     ""UsuarioId"", ""ActorRealUsuarioId"", ""ViaAcceso"", ""ViaAccesoId"", ""TipoActor"", ""FechaUtc"")
  VALUES
    (gen_random_uuid(), v_tenant, 'Usuario', p_usuario, 'Modificado',
     '{""TwoFactorEnabled"":true,""SecurityStamp"":""***""}',
     '{""TwoFactorEnabled"":false,""SecurityStamp"":""***""}',
     v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', now());

  WITH borrados AS (
    DELETE FROM public.""AspNetUserTokens""
    WHERE ""UserId"" = p_usuario
      AND ""LoginProvider"" = '[AspNetUserStore]'
      AND ""Name"" IN ('AuthenticatorKey', 'RecoveryCodes')
    RETURNING ""UserId"", ""LoginProvider"", ""Name"")
  INSERT INTO public.""RegistrosAuditoria""
    (""Id"", ""TenantId"", ""EntidadTipo"", ""EntidadId"", ""Accion"", ""DatosAntes"", ""DatosDespues"",
     ""UsuarioId"", ""ActorRealUsuarioId"", ""ViaAcceso"", ""ViaAccesoId"", ""TipoActor"", ""FechaUtc"")
  SELECT gen_random_uuid(), v_tenant, 'TokenDeUsuario', b.""UserId"", 'Eliminado',
         json_build_object('UserId', b.""UserId"", 'LoginProvider', b.""LoginProvider"",
                           'Name', b.""Name"", 'Value', '***')::text,
         NULL, v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', now()
  FROM borrados b;

  RETURN 'restablecido';
END;
$$;
REVOKE ALL ON FUNCTION app_restablecer_segundo_factor_por_soporte(uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app_restablecer_segundo_factor_por_soporte(uuid, uuid) TO cae_app_soporte;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP FUNCTION IF EXISTS app_restablecer_segundo_factor_por_soporte(uuid, uuid);

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
");
        }
    }
}
