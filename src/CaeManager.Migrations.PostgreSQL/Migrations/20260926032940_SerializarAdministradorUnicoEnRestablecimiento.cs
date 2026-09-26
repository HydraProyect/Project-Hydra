using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// ADR-011 § 8.7.3: serializa la comprobación de «Administrador único activo» de
    /// <c>app_restablecer_segundo_factor_por_soporte</c> con el alta concurrente de otro
    /// Administrador en el mismo Tenant.
    ///
    /// <para>
    /// <b>La carrera que cierra.</b> La función bloquea con <c>FOR UPDATE</c> la fila de la
    /// cuenta que restablece, pero el alta de OTRO Administrador no toca esa fila: inserta
    /// en <c>AspNetUserRoles</c> (asignación del rol) o actualiza la <c>LockoutEnd</c> de
    /// otra cuenta (reactivación). En READ COMMITTED, si esa transacción no ha confirmado
    /// cuando la función comprueba, la función no la ve, restablece, y al confirmar las dos
    /// quedan dos Administradores activos y un restablecimiento hecho por Soporte TALVEG
    /// que el propio Tenant ya podía hacer.
    /// </para>
    ///
    /// <para>
    /// <b>Por qué en la base y no en Application.</b> Un Administrador entra por varios
    /// caminos (alta y edición de usuarios, pantalla de roles, reactivación, seeders,
    /// aprovisionamiento). Un cerrojo en cada uno se olvida en el siguiente; un trigger en
    /// las dos tablas no. El cerrojo es un <c>pg_advisory_xact_lock</c> por Tenant, con una
    /// sola definición de la clave (<c>app_bloquear_administradores_de_tenant</c>) que
    /// comparten la función y el trigger, y se suelta al terminar la transacción.
    /// </para>
    ///
    /// <para>
    /// <b>Qué no hace.</b> No añade ninguna escritura ni ensancha ninguna política: el
    /// trigger solo espera. Una asignación de un rol que no es Administrador, o en otro
    /// Tenant, no toma el cerrojo. El trigger es SECURITY DEFINER para leer el Tenant de la
    /// cuenta aunque la RLS de quien asigna no la muestre: si no lo leyera, el alta no
    /// tomaría el cerrojo y la carrera seguiría abierta justo en ese camino.
    /// </para>
    /// </summary>
    public partial class SerializarAdministradorUnicoEnRestablecimiento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE FUNCTION app_bloquear_administradores_de_tenant(p_tenant uuid) RETURNS void
  LANGUAGE sql VOLATILE
  SET search_path = pg_catalog, pg_temp AS $$
  SELECT pg_advisory_xact_lock(hashtextextended('app.administradores_de_tenant:' || p_tenant::text, 0));
$$;
REVOKE ALL ON FUNCTION app_bloquear_administradores_de_tenant(uuid) FROM PUBLIC;

CREATE FUNCTION app_serializar_alta_de_administrador() RETURNS trigger
  LANGUAGE plpgsql VOLATILE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  v_tenant uuid;
BEGIN
  IF TG_TABLE_NAME = 'AspNetUserRoles' THEN
    -- Asignación del rol: solo importa si el rol es Administrador.
    IF NOT EXISTS (
        SELECT 1 FROM public.""AspNetRoles"" r
        WHERE r.""Id"" = NEW.""RoleId"" AND r.""NormalizedName"" = 'ADMINISTRADOR')
    THEN
      RETURN NEW;
    END IF;
    SELECT u.""TenantId"" INTO v_tenant FROM public.""AspNetUsers"" u WHERE u.""Id"" = NEW.""UserId"";
  ELSE
    -- Reactivación o cambio de Tenant de una cuenta: solo importa si es Administrador.
    IF NOT EXISTS (
        SELECT 1 FROM public.""AspNetUserRoles"" ur
        JOIN public.""AspNetRoles"" r ON r.""Id"" = ur.""RoleId""
        WHERE ur.""UserId"" = NEW.""Id"" AND r.""NormalizedName"" = 'ADMINISTRADOR')
    THEN
      RETURN NEW;
    END IF;
    v_tenant := NEW.""TenantId"";
  END IF;

  IF v_tenant IS NOT NULL THEN
    PERFORM public.app_bloquear_administradores_de_tenant(v_tenant);
  END IF;
  RETURN NEW;
END;
$$;
REVOKE ALL ON FUNCTION app_serializar_alta_de_administrador() FROM PUBLIC;

CREATE TRIGGER ""TR_AspNetUserRoles_SerializaAltaDeAdministrador""
  AFTER INSERT OR UPDATE OF ""RoleId"", ""UserId"" ON ""AspNetUserRoles""
  FOR EACH ROW EXECUTE FUNCTION app_serializar_alta_de_administrador();

CREATE TRIGGER ""TR_AspNetUsers_SerializaAltaDeAdministrador""
  AFTER UPDATE OF ""LockoutEnd"", ""TenantId"" ON ""AspNetUsers""
  FOR EACH ROW
  WHEN (OLD.""LockoutEnd"" IS DISTINCT FROM NEW.""LockoutEnd"" OR OLD.""TenantId"" IS DISTINCT FROM NEW.""TenantId"")
  EXECUTE FUNCTION app_serializar_alta_de_administrador();

CREATE OR REPLACE FUNCTION app_restablecer_segundo_factor_por_soporte(p_sesion uuid, p_usuario uuid) RETURNS text
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

  -- Serializa la comprobación de «Administrador único activo» con cualquier alta
  -- concurrente de otro Administrador en este Tenant (asignación del rol o
  -- reactivación de la cuenta): el trigger de esas dos vías toma el mismo cerrojo.
  -- Se toma ANTES de leer los Administradores: en READ COMMITTED cada sentencia
  -- siguiente ve lo que la otra transacción haya confirmado mientras se esperaba.
  -- Y DESPUÉS del FOR UPDATE de la cuenta, en el mismo orden que el trigger (que
  -- corre con la fila ya bloqueada): fila, luego cerrojo. Al revés, reactivar la
  -- propia cuenta objetivo mientras se restablece se interbloquearía (40P01).
  PERFORM public.app_bloquear_administradores_de_tenant(v_tenant);

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
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION app_restablecer_segundo_factor_por_soporte(p_sesion uuid, p_usuario uuid) RETURNS text
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

DROP TRIGGER IF EXISTS ""TR_AspNetUsers_SerializaAltaDeAdministrador"" ON ""AspNetUsers"";
DROP TRIGGER IF EXISTS ""TR_AspNetUserRoles_SerializaAltaDeAdministrador"" ON ""AspNetUserRoles"";
DROP FUNCTION IF EXISTS app_serializar_alta_de_administrador();
DROP FUNCTION IF EXISTS app_bloquear_administradores_de_tenant(uuid);
");
        }
    }
}
