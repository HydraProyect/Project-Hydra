namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    public partial class LineaBaseCompactada
    {
        /// <summary>
        /// Esquema que dejaban las 185 migraciones previas, generado con
        /// <c>pg_dump --schema-only</c> + <c>pg_restore -O</c> de una base migrada con ellas
        /// (origin/main 0fcb8b87) en PostgreSQL 18.6, la imagen de CI, staging y producción. En 18
        /// los NOT NULL son restricciones con nombre y el volcado conserva los que dejaron los
        /// renombrados del historial; PostgreSQL 17 acepta esa sintaxis e ignora el nombre. Sin
        /// cabecera de sesión, sin <c>__EFMigrationsHistory</c>
        /// (la crea EF), sin las particiones de auditoría ni las cuatro funciones del
        /// particionado (las crea <see cref="ParticionadoMensualEventos"/>, en el paso 3) y
        /// con las dos madres de auditoría como tablas ordinarias, que el helper convierte.
        /// <c>check_function_bodies</c> se apaga mientras dura el volcado (sus funciones SQL
        /// citan tablas que se crean después, como en <c>pg_restore</c>) y se restaura al
        /// final, para que no alcance al resto de la migración ni a otras que EF aplique en
        /// la misma transacción. <c>ALTER DEFAULT PRIVILEGES</c> sin <c>FOR ROLE</c>, como en
        /// las migraciones de origen: rige para el rol que migra.
        /// No editar a mano: el cambio de esquema va en una migración nueva.
        /// </summary>
        internal const string EsquemaSql = """
SET LOCAL check_function_bodies = false;

CREATE SCHEMA app_privado;

CREATE EXTENSION IF NOT EXISTS btree_gist WITH SCHEMA public;

CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;

CREATE FUNCTION public.app_bloquear_administradores_de_tenant(p_tenant uuid) RETURNS void
    LANGUAGE sql
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
  SELECT pg_advisory_xact_lock(hashtextextended('app.administradores_de_tenant:' || p_tenant::text, 0));
$$;

CREATE FUNCTION public.app_claves_contexto_protegidas() RETURNS TABLE(id uuid, clave_protegida bytea, valida_hasta timestamp with time zone)
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
    SELECT c.id, c.clave_protegida, c.valida_hasta
      FROM app_privado.claves_contexto c
     WHERE c.valida_hasta > now()
     ORDER BY c.registrada DESC, c.id
$$;

CREATE FUNCTION public.app_contexto_validado(OUT tenant_id uuid, OUT tenant_origen_id uuid, OUT usuario_id uuid, OUT valido boolean) RETURNS record
    LANGUAGE plpgsql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $_$
DECLARE
    token text := current_setting('app.contexto', true);
    partes text[];
    clave record;
BEGIN
    valido := false;
    IF token IS NULL OR token = '' THEN
        RETURN;
    END IF;

    partes := regexp_match(token, '^(v1\|([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\|([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})?\|([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})?\|([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})?\|([a-z]{1,16})\|([0-9]{1,10})\|([0-9]{1,12})\|([0-9a-f]{32}))\.([0-9a-f]{64})$');
    IF partes IS NULL THEN
        RAISE EXCEPTION 'contexto RLS con formato inválido' USING ERRCODE = '42501';
    END IF;

    SELECT c.ipad, c.opad INTO clave
      FROM app_privado.claves_contexto c
     WHERE c.id = partes[2]::uuid AND c.valida_hasta > now();
    IF NOT FOUND THEN
        RAISE EXCEPTION 'contexto RLS con clave desconocida o retirada' USING ERRCODE = '42501';
    END IF;

    -- Se comparan los hashes de las dos firmas, no las firmas: el tiempo de
    -- la comparación no dice cuántos bytes coinciden.
    IF sha256(sha256(clave.opad || sha256(clave.ipad || convert_to(partes[1], 'UTF8'))))
       <> sha256(decode(partes[10], 'hex')) THEN
        RAISE EXCEPTION 'contexto RLS con firma inválida' USING ERRCODE = '42501';
    END IF;

    IF partes[7]::bigint <> pg_backend_pid() THEN
        RAISE EXCEPTION 'contexto RLS firmado para otra conexión' USING ERRCODE = '42501';
    END IF;

    -- now() es el inicio de la transacción: el token tiene que estar vigente
    -- cuando empieza; dentro de ella no caduca (diseño § 7).
    IF to_timestamp(partes[8]::bigint) <= now() THEN
        RAISE EXCEPTION 'contexto RLS caducado' USING ERRCODE = '42501';
    END IF;

    tenant_id := partes[3]::uuid;
    tenant_origen_id := partes[4]::uuid;
    usuario_id := partes[5]::uuid;
    valido := true;
END;
$_$;

CREATE FUNCTION public.app_ctx_tenant_id() RETURNS uuid
    LANGUAGE sql STABLE
    AS $$ SELECT tenant_id FROM public.app_contexto_validado() $$;

CREATE FUNCTION public.app_ctx_tenant_origen_id() RETURNS uuid
    LANGUAGE sql STABLE
    AS $$ SELECT tenant_origen_id FROM public.app_contexto_validado() $$;

CREATE FUNCTION public.app_ctx_usuario_id() RETURNS uuid
    LANGUAGE sql STABLE
    AS $$ SELECT usuario_id FROM public.app_contexto_validado() $$;

CREATE FUNCTION public.app_ctx_valido() RETURNS boolean
    LANGUAGE sql STABLE
    AS $$ SELECT valido FROM public.app_contexto_validado() $$;

CREATE FUNCTION public.app_cuenta_por_nombre_normalizado(nombre_normalizado text) RETURNS TABLE(cuenta_id uuid, tenant_id uuid)
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
  SELECT u."Id", u."TenantId" FROM public."AspNetUsers" u
    WHERE u."NormalizedUserName" = nombre_normalizado;
$$;

CREATE FUNCTION public.app_cuentas_por_email_normalizado(email_normalizado text) RETURNS TABLE(cuenta_id uuid, tenant_id uuid)
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
  SELECT u."Id", u."TenantId" FROM public."AspNetUsers" u
    WHERE u."NormalizedEmail" = email_normalizado;
$$;

CREATE FUNCTION public.app_es_admin_plataforma(usuario uuid) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
  SELECT EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
    WHERE c."UsuarioPlataformaId" = usuario AND c."Capacidad" = 'AdminPlataforma'
      AND c."Estado" = 'Vigente' AND c."VigenciaDesde" <= now()
      AND (c."VigenciaHasta" IS NULL OR now() < c."VigenciaHasta"));
$$;

CREATE FUNCTION public.app_es_admin_plataforma_global(usuario uuid) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
  SELECT EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
    WHERE c."UsuarioPlataformaId" = usuario AND c."Capacidad" = 'AdminPlataforma'
      AND c."EsAlcanceGlobal"
      AND c."Estado" = 'Vigente' AND c."VigenciaDesde" <= now()
      AND (c."VigenciaHasta" IS NULL OR now() < c."VigenciaHasta"));
$$;

CREATE FUNCTION public.app_es_admin_plataforma_sobre(usuario uuid, tenant uuid) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
  SELECT EXISTS (SELECT 1 FROM public."ConcesionesPrivilegio" c
    WHERE c."UsuarioPlataformaId" = usuario AND c."Capacidad" = 'AdminPlataforma'
      AND c."Estado" = 'Vigente' AND c."VigenciaDesde" <= now()
      AND (c."VigenciaHasta" IS NULL OR now() < c."VigenciaHasta")
      AND (c."EsAlcanceGlobal" OR EXISTS (SELECT 1 FROM public."TenantsAlcanzadosPorConcesion" t
           WHERE t."ConcesionPrivilegioId" = c."Id" AND t."TenantId" = tenant)));
$$;

CREATE FUNCTION public.app_es_administrador_del_tenant(usuario uuid, tenant uuid) RETURNS boolean
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
  SELECT EXISTS (
    SELECT 1
    FROM public."AspNetUsers" u
    JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
    JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
    WHERE u."Id" = usuario
      AND u."TenantId" = tenant
      AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= now() + interval '365 days')
      AND r."NormalizedName" = 'ADMINISTRADOR');
$$;

CREATE FUNCTION public.app_restablecer_segundo_factor_por_soporte(p_sesion uuid, p_usuario uuid) RETURNS text
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
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
      FROM public."SesionesPrivilegiadas" s
      JOIN public."ConcesionesPrivilegio" c ON c."Id" = s."ConcesionPrivilegioId"
      WHERE s."Id" = p_sesion
        AND s."TenantObjetivoId" = v_tenant
        AND s."UsuarioSimuladoId" IS NULL
        AND s."CerradaEnUtc" IS NULL
        AND s."InicioEnUtc" <= now()
        AND s."ExpiraEnUtc" > now()
        AND c."UsuarioPlataformaId" = v_actor
        AND c."Capacidad" = 'RestablecimientoSegundoFactor'
        AND c."Estado" = 'Vigente'
        AND c."EsAlcanceGlobal" = false
        AND c."VigenciaDesde" <= now()
        AND (c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > now())
        AND c."ConcedidaPorUsuarioId" IS NOT NULL
        AND c."ConcedidaPorUsuarioId" <> c."UsuarioPlataformaId"
        AND EXISTS (
            SELECT 1 FROM public."TenantsAlcanzadosPorConcesion" t
            WHERE t."ConcesionPrivilegioId" = c."Id" AND t."TenantId" = v_tenant))
  THEN
    RETURN 'sesion_no_autorizada';
  END IF;

  -- Quien la ejerce es un Actor de Plataforma TALVEG: su cuenta es de un Tenant
  -- de plataforma. EsPlataforma aquí solo restringe, no concede nada.
  IF NOT EXISTS (
      SELECT 1 FROM public."AspNetUsers" a
      JOIN public."Tenants" tp ON tp."Id" = a."TenantId"
      WHERE a."Id" = v_actor AND tp."EsPlataforma")
  THEN
    RETURN 'sesion_no_autorizada';
  END IF;

  -- La cuenta: del Tenant del contexto y no desactivada (mismo umbral que
  -- ApplicationUser.UmbralDeCuentaDesactivada). La fila queda bloqueada hasta el final.
  SELECT u."TwoFactorEnabled" INTO v_dos_factores
  FROM public."AspNetUsers" u
  WHERE u."Id" = p_usuario
    AND u."TenantId" = v_tenant
    AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= now() + interval '365 days')
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
      SELECT 1 FROM public."AspNetUserRoles" ur
      JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
      WHERE ur."UserId" = p_usuario AND r."NormalizedName" = 'ADMINISTRADOR')
  THEN
    RETURN 'no_es_administrador';
  END IF;

  IF EXISTS (
      SELECT 1 FROM public."AspNetUsers" o
      JOIN public."AspNetUserRoles" ur ON ur."UserId" = o."Id"
      JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
      WHERE o."TenantId" = v_tenant
        AND o."Id" <> p_usuario
        AND r."NormalizedName" = 'ADMINISTRADOR'
        AND (o."LockoutEnd" IS NULL OR o."LockoutEnd" <= now() + interval '365 days'))
  THEN
    RETURN 'hay_otro_administrador';
  END IF;

  IF NOT v_dos_factores THEN
    RETURN 'sin_segundo_factor';
  END IF;

  -- El acto: lo mismo que el camino del Administrador (P0-8). El sello nuevo
  -- cierra las sesiones abiertas de la cuenta en su siguiente validación.
  UPDATE public."AspNetUsers"
  SET "TwoFactorEnabled" = false,
      "SecurityStamp" = upper(replace(gen_random_uuid()::text, '-', '')),
      "ConcurrencyStamp" = gen_random_uuid()::text
  WHERE "Id" = p_usuario;

  -- Auditoría con la forma de AuditoriaInterceptor (sello y valor de token
  -- enmascarados), más la vía y la sesión que ampara el acto. Actor real =
  -- técnico de Soporte TALVEG; la cuenta afectada va en EntidadId.
  INSERT INTO public."RegistrosAuditoria"
    ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
     "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
  VALUES
    (gen_random_uuid(), v_tenant, 'Usuario', p_usuario, 'Modificado',
     '{"TwoFactorEnabled":true,"SecurityStamp":"***"}',
     '{"TwoFactorEnabled":false,"SecurityStamp":"***"}',
     v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', now());

  WITH borrados AS (
    DELETE FROM public."AspNetUserTokens"
    WHERE "UserId" = p_usuario
      AND "LoginProvider" = '[AspNetUserStore]'
      AND "Name" IN ('AuthenticatorKey', 'RecoveryCodes')
    RETURNING "UserId", "LoginProvider", "Name")
  INSERT INTO public."RegistrosAuditoria"
    ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
     "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
  SELECT gen_random_uuid(), v_tenant, 'TokenDeUsuario', b."UserId", 'Eliminado',
         json_build_object('UserId', b."UserId", 'LoginProvider', b."LoginProvider",
                           'Name', b."Name", 'Value', '***')::text,
         NULL, v_actor, v_actor, 'SesionPrivilegiada', p_sesion, 'Persona', now()
  FROM borrados b;

  RETURN 'restablecido';
END;
$$;

CREATE FUNCTION public.app_serializar_alta_de_administrador() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
DECLARE
  v_tenant uuid;
BEGIN
  IF TG_TABLE_NAME = 'AspNetUserRoles' THEN
    -- Asignación del rol: solo importa si el rol es Administrador.
    IF NOT EXISTS (
        SELECT 1 FROM public."AspNetRoles" r
        WHERE r."Id" = NEW."RoleId" AND r."NormalizedName" = 'ADMINISTRADOR')
    THEN
      RETURN NEW;
    END IF;
    SELECT u."TenantId" INTO v_tenant FROM public."AspNetUsers" u WHERE u."Id" = NEW."UserId";
  ELSE
    -- Reactivación o cambio de Tenant de una cuenta: solo importa si es Administrador.
    IF NOT EXISTS (
        SELECT 1 FROM public."AspNetUserRoles" ur
        JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
        WHERE ur."UserId" = NEW."Id" AND r."NormalizedName" = 'ADMINISTRADOR')
    THEN
      RETURN NEW;
    END IF;
    v_tenant := NEW."TenantId";
  END IF;

  IF v_tenant IS NOT NULL THEN
    PERFORM public.app_bloquear_administradores_de_tenant(v_tenant);
  END IF;
  RETURN NEW;
END;
$$;

CREATE FUNCTION public.app_tenant_de_clave_api(hash_clave text) RETURNS uuid
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
  SELECT c."TenantId" FROM public."ClavesApi" c
    WHERE c."HashClave" = hash_clave AND c."EstaEliminado" = false;
$$;

CREATE FUNCTION public.app_tenant_de_cuenta(cuenta_id uuid) RETURNS uuid
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'pg_temp'
    AS $$
  SELECT u."TenantId" FROM public."AspNetUsers" u WHERE u."Id" = cuenta_id;
$$;

CREATE FUNCTION public.paso_tarea_asistente_exige_plan_confirmado() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW."Estado" IN ('Confirmado', 'Ejecutado', 'Fallido') AND NOT EXISTS (
        SELECT 1 FROM "TareasAsistente" t
        WHERE t."Id" = NEW."TareaAsistenteId"
          AND t."TenantId" = NEW."TenantId"
          AND t."PlanConfirmadoEnUtc" IS NOT NULL)
    THEN
        RAISE EXCEPTION 'El paso % no se confirma ni se ejecuta sin un plan confirmado.', NEW."Id"
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NULL;
END;
$$;

CREATE FUNCTION public.tarea_asistente_confirmacion_inmutable() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF OLD."PlanConfirmadoEnUtc" IS NOT NULL
       AND (NEW."PlanConfirmadoEnUtc" IS DISTINCT FROM OLD."PlanConfirmadoEnUtc"
            OR NEW."PlanConfirmadoPorActorRealUsuarioId" IS DISTINCT FROM OLD."PlanConfirmadoPorActorRealUsuarioId"
            OR NEW."PlanConfirmadoComoUsuarioSimuladoId" IS DISTINCT FROM OLD."PlanConfirmadoComoUsuarioSimuladoId")
    THEN
        RAISE EXCEPTION 'La confirmación del plan de la tarea % no se modifica.', OLD."Id"
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TABLE app_privado.claves_contexto (
    id uuid NOT NULL,
    ipad bytea NOT NULL,
    opad bytea NOT NULL,
    clave_protegida bytea NOT NULL,
    valida_hasta timestamp with time zone NOT NULL,
    registrada timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT claves_contexto_clave_protegida_check CHECK ((octet_length(clave_protegida) > 0)),
    CONSTRAINT claves_contexto_ipad_check CHECK ((octet_length(ipad) = 64)),
    CONSTRAINT claves_contexto_opad_check CHECK ((octet_length(opad) = 64))
);

CREATE TABLE public."AceptacionesTerminos" (
    "Id" uuid NOT NULL,
    "UsuarioId" uuid NOT NULL,
    "VersionDocumento" character varying(20) NOT NULL,
    "FechaAceptacionUtc" timestamp with time zone NOT NULL
);

CREATE TABLE public."AcreditacionesDocumentoPlataforma" (
    "Id" uuid NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "CanalGestionDocumentalId" uuid CONSTRAINT "AcreditacionesDocumentoPlataf_CanalGestionDocumentalId_not_null" NOT NULL,
    "Estado" integer NOT NULL,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid,
    "EstadoVigencia" integer DEFAULT 0 NOT NULL,
    "FechaVencimientoEnPlataforma" date
);

ALTER TABLE ONLY public."AcreditacionesDocumentoPlataforma" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."AdjuntosMensaje" (
    "Id" uuid CONSTRAINT "AdjuntosMensajeCorreo_Id_not_null" NOT NULL,
    "MensajeId" uuid CONSTRAINT "AdjuntosMensajeCorreo_MensajeCorreoId_not_null" NOT NULL,
    "NombreArchivo" character varying(260) CONSTRAINT "AdjuntosMensajeCorreo_NombreArchivo_not_null" NOT NULL,
    "TipoContenido" character varying(150) CONSTRAINT "AdjuntosMensajeCorreo_TipoContenido_not_null" NOT NULL,
    "TamanoBytes" bigint CONSTRAINT "AdjuntosMensajeCorreo_TamanoBytes_not_null" NOT NULL,
    "ArchivoUrl" character varying(500) CONSTRAINT "AdjuntosMensajeCorreo_ArchivoUrl_not_null" NOT NULL,
    "TenantId" uuid CONSTRAINT "AdjuntosMensajeCorreo_TenantId_not_null" NOT NULL
);

ALTER TABLE ONLY public."AdjuntosMensaje" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."AprobacionesDocumento" (
    "Id" uuid NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "Tipo" integer NOT NULL,
    "ConfianzaGeneral" integer NOT NULL,
    "UsuarioId" uuid,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."AprobacionesDocumento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."Asignaciones" (
    "Id" uuid NOT NULL,
    "TrabajadorId" uuid NOT NULL,
    "CentroId" uuid NOT NULL,
    "FechaAlta" date NOT NULL,
    "FechaBaja" date,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."Asignaciones" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."AsignacionesCartera" (
    "Id" uuid NOT NULL,
    "AsignacionOperacionId" uuid NOT NULL,
    "UsuarioId" uuid NOT NULL,
    "Rol" character varying(50),
    "PropietarioTenantId" uuid NOT NULL,
    "OperadorTenantId" uuid NOT NULL,
    "AmbitoRelacionClienteId" uuid,
    "AmbitoCentroId" uuid,
    "AmbitoTrabajadorId" uuid,
    "AmbitoProyectoId" uuid,
    "VigenciaDesde" timestamp with time zone NOT NULL,
    "VigenciaHasta" timestamp with time zone,
    "Estado" character varying(20) NOT NULL,
    "MotivoCierre" character varying(30),
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "CreadoPorUsuarioId" uuid
);

CREATE TABLE public."AsignacionesOperacion" (
    "Id" uuid NOT NULL,
    "EsRaiz" boolean NOT NULL,
    "Servicio" character varying(20) NOT NULL,
    "PropietarioTenantId" uuid NOT NULL,
    "OperadorTenantId" uuid NOT NULL,
    "AmbitoRelacionClienteId" uuid,
    "AmbitoCentroId" uuid,
    "AmbitoTrabajadorId" uuid,
    "AmbitoProyectoId" uuid,
    "VigenciaDesde" timestamp with time zone NOT NULL,
    "VigenciaHasta" timestamp with time zone,
    "Estado" character varying(20) NOT NULL,
    "MotivoCierre" character varying(30),
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "CreadoPorUsuarioId" uuid
);

CREATE TABLE public."AsignacionesOperadorDelegado" (
    "Id" uuid NOT NULL,
    "DelegacionTenantId" uuid NOT NULL,
    "UsuarioId" uuid NOT NULL,
    "Rol" character varying(50) NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "MotivoRevocacion" character varying(200),
    "RevocadaEnUtc" timestamp with time zone
);

CREATE TABLE public."AspNetRoleClaims" (
    "Id" integer NOT NULL,
    "RoleId" uuid NOT NULL,
    "ClaimType" text,
    "ClaimValue" text
);

ALTER TABLE public."AspNetRoleClaims" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."AspNetRoleClaims_Id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);

CREATE TABLE public."AspNetRoles" (
    "Id" uuid NOT NULL,
    "Name" character varying(256),
    "NormalizedName" character varying(256),
    "ConcurrencyStamp" text
);

CREATE TABLE public."AspNetUserClaims" (
    "Id" integer NOT NULL,
    "UserId" uuid NOT NULL,
    "ClaimType" text,
    "ClaimValue" text
);

ALTER TABLE public."AspNetUserClaims" ALTER COLUMN "Id" ADD GENERATED BY DEFAULT AS IDENTITY (
    SEQUENCE NAME public."AspNetUserClaims_Id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);

CREATE TABLE public."AspNetUserLogins" (
    "LoginProvider" text NOT NULL,
    "ProviderKey" text NOT NULL,
    "ProviderDisplayName" text,
    "UserId" uuid NOT NULL
);

CREATE TABLE public."AspNetUserRoles" (
    "UserId" uuid NOT NULL,
    "RoleId" uuid NOT NULL
);

CREATE TABLE public."AspNetUserTokens" (
    "UserId" uuid NOT NULL,
    "LoginProvider" text NOT NULL,
    "Name" text NOT NULL,
    "Value" text
);

CREATE TABLE public."AspNetUsers" (
    "Id" uuid NOT NULL,
    "NombreCompleto" text NOT NULL,
    "Tema" integer NOT NULL,
    "CoordinadorUsuarioId" uuid,
    "ClienteId" uuid,
    "DebeCambiarContrasena" boolean NOT NULL,
    "FechaCreacion" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "UserName" character varying(256),
    "NormalizedUserName" character varying(256),
    "Email" character varying(256),
    "NormalizedEmail" character varying(256),
    "EmailConfirmed" boolean NOT NULL,
    "PasswordHash" text,
    "SecurityStamp" text,
    "ConcurrencyStamp" text,
    "PhoneNumber" text,
    "PhoneNumberConfirmed" boolean NOT NULL,
    "TwoFactorEnabled" boolean NOT NULL,
    "LockoutEnd" timestamp with time zone,
    "LockoutEnabled" boolean NOT NULL,
    "AccessFailedCount" integer NOT NULL,
    "UltimaActividadUtc" timestamp with time zone,
    "PermisoConsultarAccesoDocumentosSensibles" boolean DEFAULT false NOT NULL,
    "Idioma" integer DEFAULT 0 NOT NULL
);

CREATE TABLE public."AuditoriasExtraccionIa" (
    "Id" uuid NOT NULL,
    "HashSha256" character varying(64) NOT NULL,
    "TipoEsperado" character varying(150) NOT NULL,
    "ProveedorCodigo" character varying(100) NOT NULL,
    "TiempoProcesamientoMs" bigint NOT NULL,
    "CosteEstimadoOcr" numeric(10,6),
    "CosteEstimado" numeric(10,6),
    "NumeroPaginas" integer NOT NULL,
    "ConfianzaGeneral" integer NOT NULL,
    "Incidencias" character varying(1000),
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "DecisionHumana" integer,
    "DocumentoId" uuid,
    "FechaDecisionUtc" timestamp with time zone,
    "UsuarioDecisionId" uuid,
    "ModeloExacto" character varying(150),
    "ProveedoresInvocados" character varying(300),
    "RequestId" character varying(200),
    "VersionPipeline" character varying(40) DEFAULT ''::character varying NOT NULL
);

ALTER TABLE ONLY public."AuditoriasExtraccionIa" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."AvisosRevisionNormativa" (
    "Id" uuid NOT NULL,
    "IdentificadorBoe" character varying(50) NOT NULL,
    "FechaPublicacion" date NOT NULL,
    "Titulo" character varying(500) NOT NULL,
    "UrlHtml" character varying(500) NOT NULL,
    "NormaVigilada" character varying(100) NOT NULL,
    "DetectadoEnUtc" timestamp with time zone NOT NULL,
    "Revisado" boolean NOT NULL,
    "RevisadoEnUtc" timestamp with time zone,
    "RevisadoPorUsuarioId" uuid,
    "NotasRevision" character varying(1000),
    "Version" uuid DEFAULT '00000000-0000-0000-0000-000000000000'::uuid NOT NULL
);

CREATE TABLE public."CanalesGestionDocumental" (
    "Id" uuid NOT NULL,
    "CentroId" uuid NOT NULL,
    "Tipo" integer NOT NULL,
    "UrlAcceso" character varying(500),
    "Usuario" text,
    "Contrasena" text,
    "EmailsDestinatarios" character varying(500),
    "NombreContacto" character varying(150),
    "Notas" character varying(1000),
    "TenantId" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone DEFAULT now() NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid,
    "EsPrincipal" boolean DEFAULT false NOT NULL,
    "EstaEliminado" boolean DEFAULT false NOT NULL,
    "EtiquetaProposito" character varying(150) DEFAULT 'Gestión general'::character varying NOT NULL,
    "Version" uuid DEFAULT gen_random_uuid() NOT NULL,
    "ProveedorPlataformaCaeId" uuid
);

ALTER TABLE ONLY public."CanalesGestionDocumental" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."Centros" (
    "Id" uuid NOT NULL,
    "ClienteId" uuid NOT NULL,
    "EmpresaId" uuid NOT NULL,
    "Nombre" character varying(200) NOT NULL,
    "CodigoCentro" character varying(50),
    "Direccion" character varying(300),
    "Contacto" character varying(500),
    "ContratoVigenteHasta" date,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid,
    "GestionCae" integer DEFAULT 0 NOT NULL
);

ALTER TABLE ONLY public."Centros" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ClasificacionesRelevanciaCae" (
    "Id" uuid NOT NULL,
    "ConversacionId" uuid NOT NULL,
    "EsAccionableCae" boolean NOT NULL,
    "Resumen" text NOT NULL,
    "Confianza" integer NOT NULL,
    "ActualizadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."ClasificacionesRelevanciaCae" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ClasificacionesRuidoDetalleGestion" (
    "Id" uuid NOT NULL,
    "DetalleSugerenciaGestionCorreoId" uuid CONSTRAINT "ClasificacionesRuidoDetalle_DetalleSugerenciaGestionCo_not_null" NOT NULL,
    "ReclamacionDocumentalDocumentoId" uuid CONSTRAINT "ClasificacionesRuidoDetalle_ReclamacionDocumentalDocum_not_null" NOT NULL,
    "ConfirmadaManualmente" boolean CONSTRAINT "ClasificacionesRuidoDetalleGesti_ConfirmadaManualmente_not_null" NOT NULL,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."ClasificacionesRuidoDetalleGestion" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ClasificacionesRuidoMensaje" (
    "Id" uuid NOT NULL,
    "MensajeId" uuid NOT NULL,
    "EsNotificacionAutomatica" boolean NOT NULL,
    "ProveedorPlataformaCaeId" uuid,
    "Motivo" integer NOT NULL,
    "ConfirmadaManualmente" boolean NOT NULL,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."ClasificacionesRuidoMensaje" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ClavesApi" (
    "Id" uuid NOT NULL,
    "NombreDescriptivo" character varying(200) NOT NULL,
    "PrefijoVisible" character varying(16) NOT NULL,
    "HashClave" character varying(64) NOT NULL,
    "CreadaPorUsuarioId" uuid NOT NULL,
    "UltimoUsoUtc" timestamp with time zone,
    "RevocadaEnUtc" timestamp with time zone,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid
);

ALTER TABLE ONLY public."ClavesApi" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ConcesionesPrivilegio" (
    "Id" uuid NOT NULL,
    "UsuarioPlataformaId" uuid NOT NULL,
    "Capacidad" character varying(30) NOT NULL,
    "EsAlcanceGlobal" boolean NOT NULL,
    "VigenciaDesde" timestamp with time zone NOT NULL,
    "VigenciaHasta" timestamp with time zone,
    "Estado" character varying(20) NOT NULL,
    "ConcedidaPorUsuarioId" uuid,
    "MotivoConcesion" character varying(500),
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "Origen" character varying(25) DEFAULT ''::character varying NOT NULL,
    CONSTRAINT "CK_ConcesionesPrivilegio_AlcanceGlobalSoloCapacidadesAdmitidas" CHECK (((NOT "EsAlcanceGlobal") OR ("Capacidad" IN ('AdminPlataforma', 'SoporteLectura')))),
    CONSTRAINT "CK_ConcesionesPrivilegio_SoporteGlobalConVigenciaFinita" CHECK (((NOT ("EsAlcanceGlobal" AND (("Capacidad")::text = 'SoporteLectura'::text))) OR ("VigenciaHasta" IS NOT NULL)))
);

ALTER TABLE ONLY public."ConcesionesPrivilegio" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ConexionesIntegracion" (
    "Id" uuid NOT NULL,
    "ClienteId" uuid,
    "BuzonEmail" character varying(320) NOT NULL,
    "Nombre" character varying(200) NOT NULL,
    "Estado" integer NOT NULL,
    "UltimoError" character varying(1000),
    "FechaConectadaUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid,
    "Proveedor" integer DEFAULT 0 NOT NULL,
    "GestorPropietarioId" uuid
);

ALTER TABLE ONLY public."ConexionesIntegracion" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ConfiguracionesIaDocumentoCliente" (
    "Id" uuid NOT NULL,
    "ClienteId" uuid NOT NULL,
    "TipoDocumentoId" uuid NOT NULL,
    "Activa" boolean NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."ConfiguracionesIaDocumentoCliente" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ConocimientosDeteccionCampo" (
    "Id" uuid NOT NULL,
    "EtiquetaNormalizada" character varying(200) NOT NULL,
    "FuenteDatoCandidata" text NOT NULL,
    "Prioridad" integer NOT NULL
);

CREATE TABLE public."ContactosAgenda" (
    "Id" uuid NOT NULL,
    "ClienteId" uuid,
    "EmpresaId" uuid,
    "SubcontrataId" uuid,
    "CentroId" uuid,
    "Nombre" character varying(200) NOT NULL,
    "Email" character varying(320) NOT NULL,
    "Telefono" character varying(20),
    "Cargo" character varying(150),
    "Notas" character varying(1000),
    "EsPredeterminado" boolean NOT NULL,
    "RecibeProgramacionVisitas" boolean NOT NULL,
    "RecibeFacturacion" boolean NOT NULL,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid,
    CONSTRAINT "CK_ContactosAgenda_PropietarioXor" CHECK ((num_nonnulls("ClienteId", "EmpresaId", "SubcontrataId", "CentroId") = 1))
);

ALTER TABLE ONLY public."ContactosAgenda" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ContactosAgendaRoles" (
    "Id" uuid NOT NULL,
    "ContactoAgendaId" uuid NOT NULL,
    "Rol" text NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."ContactosAgendaRoles" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ContactosAgendaTiposDocumento" (
    "Id" uuid NOT NULL,
    "ContactoAgendaId" uuid NOT NULL,
    "TipoDocumentoId" uuid NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."ContactosAgendaTiposDocumento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ContactosWhatsApp" (
    "Id" uuid NOT NULL,
    "Telefono" character varying(20) NOT NULL,
    "ClienteId" uuid NOT NULL,
    "Nombre" character varying(200),
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."ContactosWhatsApp" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."Conversaciones" (
    "Id" uuid CONSTRAINT "ConversacionesCorreo_Id_not_null" NOT NULL,
    "ClienteId" uuid,
    "Asunto" character varying(300) CONSTRAINT "ConversacionesCorreo_Asunto_not_null" NOT NULL,
    "Estado" integer CONSTRAINT "ConversacionesCorreo_Estado_not_null" NOT NULL,
    "EjecutivoAsignadoId" uuid,
    "Etiquetas" character varying(500),
    "FechaUltimoMensajeUtc" timestamp with time zone CONSTRAINT "ConversacionesCorreo_FechaUltimoMensajeUtc_not_null" NOT NULL,
    "TenantId" uuid CONSTRAINT "ConversacionesCorreo_TenantId_not_null" NOT NULL,
    "Version" uuid CONSTRAINT "ConversacionesCorreo_Version_not_null" NOT NULL,
    "CreadoEnUtc" timestamp with time zone CONSTRAINT "ConversacionesCorreo_CreadoEnUtc_not_null" NOT NULL,
    "EstaEliminado" boolean CONSTRAINT "ConversacionesCorreo_EstaEliminado_not_null" NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid,
    "ConexionIntegracionId" uuid,
    "HiloExternoId" character varying(300),
    "Canal" integer DEFAULT 0 CONSTRAINT "ConversacionesCorreo_Canal_not_null" NOT NULL,
    "FechaUltimoMensajeEntranteUtc" timestamp with time zone,
    "TelefonoContacto" character varying(20),
    "EmpresaId" uuid,
    CONSTRAINT "CK_Conversaciones_AnclaUnica" CHECK ((num_nonnulls("ClienteId", "EmpresaId") <= 1))
);

ALTER TABLE ONLY public."Conversaciones" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."CredencialesAccesoEmpresa" (
    "Id" uuid NOT NULL,
    "EmpresaId" uuid NOT NULL,
    "UrlAcceso" character varying(500),
    "CampoEmpresa" character varying(200),
    "Usuario" text,
    "Contrasena" text,
    "Notas" character varying(1000),
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."CredencialesAccesoEmpresa" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."CredencialesAccesoSubcontrata" (
    "Id" uuid NOT NULL,
    "SubcontrataId" uuid NOT NULL,
    "UrlAcceso" character varying(500),
    "CampoEmpresa" character varying(200),
    "Usuario" text,
    "Contrasena" text,
    "Notas" character varying(1000),
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."CredencialesAccesoSubcontrata" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."CredencialesIntegracion" (
    "Id" uuid NOT NULL,
    "ConexionIntegracionId" uuid NOT NULL,
    "RefreshToken" text NOT NULL,
    "TenantId" uuid NOT NULL,
    "Version" uuid DEFAULT '00000000-0000-0000-0000-000000000000'::uuid NOT NULL,
    "AccessToken" text,
    "AccessTokenExpiraUtc" timestamp with time zone
);

ALTER TABLE ONLY public."CredencialesIntegracion" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."DelegacionesTenant" (
    "Id" uuid NOT NULL,
    "TenantConsultoraId" uuid NOT NULL,
    "TenantClienteId" uuid NOT NULL,
    "Activa" boolean NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "Proposito" character varying(20) NOT NULL,
    "MotivoActivacion" character varying(500),
    "ExpiraEnUtc" timestamp with time zone,
    "ActivadaEnUtc" timestamp with time zone
);

CREATE TABLE public."DetallesSugerenciaGestionCorreo" (
    "Id" uuid NOT NULL,
    "SugerenciaGestionCorreoId" uuid CONSTRAINT "DetallesSugerenciaGestionCor_SugerenciaGestionCorreoId_not_null" NOT NULL,
    "TrabajadorId" uuid,
    "TipoDocumentoId" uuid,
    "ConfianzaTrabajador" integer NOT NULL,
    "ConfianzaTipoDocumento" integer NOT NULL,
    "Resuelta" boolean NOT NULL,
    "TenantId" uuid NOT NULL,
    "Resolucion" integer,
    "ResueltaEnUtc" timestamp with time zone,
    "ResueltaPorUsuarioId" uuid
);

ALTER TABLE ONLY public."DetallesSugerenciaGestionCorreo" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."DeteccionesTrabajador" (
    "Id" uuid NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "EmpresaId" uuid NOT NULL,
    "Tipo" integer NOT NULL,
    "Nombre" character varying(100) NOT NULL,
    "Apellidos" character varying(150) NOT NULL,
    "Dni" character varying(20) NOT NULL,
    "TrabajadorExistenteId" uuid,
    "Resuelta" boolean NOT NULL,
    "AccionTomada" text,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."DeteccionesTrabajador" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."Documentos" (
    "Id" uuid NOT NULL,
    "TrabajadorId" uuid,
    "ClienteId" uuid,
    "EmpresaId" uuid,
    "VehiculoId" uuid,
    "ProyectoId" uuid,
    "TipoDocumentoId" uuid NOT NULL,
    "FechaEmision" date NOT NULL,
    "FechaVencimiento" date,
    "ArchivoUrl" character varying(500),
    "Comentarios" character varying(1000),
    "AnonimizadoEnUtc" timestamp with time zone,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid,
    "EstadoVigencia" integer DEFAULT 0 NOT NULL,
    CONSTRAINT "CK_Documentos_EstadoVigenciaCoherente" CHECK (((("EstadoVigencia" = 2) AND ("FechaVencimiento" IS NOT NULL)) OR (("EstadoVigencia" = ANY (ARRAY[0, 1])) AND ("FechaVencimiento" IS NULL)))),
    CONSTRAINT "CK_Documentos_PropietarioXor" CHECK ((num_nonnulls("TrabajadorId", "ClienteId", "EmpresaId", "VehiculoId", "ProyectoId") = 1))
);

ALTER TABLE ONLY public."Documentos" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."DocumentosGenerados" (
    "Id" uuid NOT NULL,
    "PlantillaDocumentoVersionId" uuid NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "TrabajadorId" uuid,
    "EmpresaId" uuid,
    "CentroId" uuid,
    "DatosUtilizadosJson" text NOT NULL,
    "GeneradoPorUsuarioId" uuid NOT NULL,
    "GeneradoEnUtc" timestamp with time zone NOT NULL,
    "Estado" text NOT NULL,
    "CodigoSeguroVerificacion" character varying(100),
    "HashSha256Impreso" character varying(64),
    "SelloElectronicoAplicadoEnUtc" timestamp with time zone,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid
);

ALTER TABLE ONLY public."DocumentosGenerados" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."DominiosProveedorPlataformaCae" (
    "Id" uuid NOT NULL,
    "ProveedorPlataformaCaeId" uuid CONSTRAINT "DominiosProveedorPlataformaCa_ProveedorPlataformaCaeId_not_null" NOT NULL,
    "Dominio" character varying(200) NOT NULL
);

CREATE TABLE public."Empresas" (
    "Id" uuid NOT NULL,
    "RazonSocial" character varying(200) NOT NULL,
    "Cif" character varying(9),
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid,
    "Cnae" character varying(10),
    "ConvenioAplicable" character varying(300),
    "EsActividadAnexoI" boolean DEFAULT false NOT NULL,
    "EjecutivoUsuarioId" uuid,
    "EsCritico" boolean,
    "EsPropia" boolean DEFAULT true NOT NULL,
    "NivelServicio" character varying(50),
    "Notas" character varying(2000)
);

ALTER TABLE ONLY public."Empresas" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."EstadoBootstrapPlataforma" (
    "Id" uuid NOT NULL,
    "UsuarioRaizId" uuid NOT NULL,
    "DesignadaEnUtc" timestamp with time zone NOT NULL,
    "Consumido" boolean NOT NULL,
    "ConsumidoEnUtc" timestamp with time zone,
    "Version" uuid NOT NULL,
    CONSTRAINT "CK_EstadoBootstrapPlataforma_FilaUnica" CHECK (("Id" = 'b0075742-0000-4000-8000-000000000001'::uuid))
);

ALTER TABLE ONLY public."EstadoBootstrapPlataforma" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."EstadosAutomatizacion" (
    "Id" uuid NOT NULL,
    "TrabajoId" character varying(100) NOT NULL,
    "Activo" boolean NOT NULL,
    "UltimaEjecucionUtc" timestamp with time zone,
    "UltimoResultadoExitoso" boolean,
    "TenantId" uuid NOT NULL,
    "UltimoMensajeError" character varying(1000),
    "UltimosElementosAfectados" integer,
    "UltimosElementosEvaluados" integer
);

ALTER TABLE ONLY public."EstadosAutomatizacion" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."EventosConversacion" (
    "Id" uuid NOT NULL,
    "ConversacionId" uuid NOT NULL,
    "Tipo" integer NOT NULL,
    "ReferenciaId" uuid NOT NULL,
    "FechaUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."EventosConversacion" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."EventosRecientesUsuario" (
    "Id" uuid NOT NULL,
    "UsuarioId" uuid NOT NULL,
    "Tipo" character varying(30) NOT NULL,
    "EntidadId" uuid,
    "Titulo" character varying(200) NOT NULL,
    "Subtitulo" character varying(200),
    "UrlDestino" character varying(500) NOT NULL,
    "OcurridoEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."EventosRecientesUsuario" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."EventosWebhook" (
    "Id" uuid NOT NULL,
    "ConexionIntegracionId" uuid NOT NULL,
    "PayloadCrudo" text NOT NULL,
    "Intentos" integer NOT NULL,
    "ErrorProcesado" character varying(1000),
    "FechaRecepcionUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "Estado" text NOT NULL,
    "IniciadoEnUtc" timestamp with time zone,
    "SiguienteIntentoEnUtc" timestamp with time zone,
    "PayloadRedactado" boolean DEFAULT false NOT NULL
);

ALTER TABLE ONLY public."EventosWebhook" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ExtraccionesIaCache" (
    "Id" uuid NOT NULL,
    "HashSha256" character varying(64) NOT NULL,
    "ExtraccionJson" text NOT NULL,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "TipoEsperado" character varying(150) DEFAULT ''::character varying NOT NULL,
    "VersionPipeline" character varying(40) DEFAULT ''::character varying NOT NULL
);

ALTER TABLE ONLY public."ExtraccionesIaCache" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ExtraccionesIaCacheDocumentos" (
    "Id" uuid NOT NULL,
    "ExtraccionIaCacheId" uuid NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."ExtraccionesIaCacheDocumentos" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."FiltrosGuardados" (
    "Id" uuid NOT NULL,
    "UsuarioId" uuid NOT NULL,
    "Pantalla" character varying(50) NOT NULL,
    "Nombre" character varying(100) NOT NULL,
    "ValoresJson" text NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL
);

CREATE TABLE public."FirmasDigitalesDocumento" (
    "Id" uuid NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "Indice" integer NOT NULL,
    "Estado" text NOT NULL,
    "CadenaConfiable" boolean NOT NULL,
    "Revocacion" text NOT NULL,
    "EmisorCertificado" character varying(300) NOT NULL,
    "NumeroSerieCertificado" character varying(100) NOT NULL,
    "EsSelloDeOrgano" boolean NOT NULL,
    "FirmanteNombre" character varying(300),
    "FirmanteNif" character varying(20),
    "FechaFirmaUtc" timestamp with time zone,
    "TieneSelloDeTiempo" boolean NOT NULL,
    "CubreDocumentoCompleto" boolean NOT NULL,
    "MotivoInvalidez" character varying(500),
    "VerificadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."FirmasDigitalesDocumento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."FirmasEnCampoDocumento" (
    "Id" uuid NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "FirmanteUsuarioId" uuid NOT NULL,
    "FirmanteNombre" character varying(300) NOT NULL,
    "FirmanteRol" character varying(50) NOT NULL,
    "FirmadoEnUtc" timestamp with time zone NOT NULL,
    "Ubicacion" character varying(300),
    "HashSha256Pdf" character varying(64) NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."FirmasEnCampoDocumento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."FirmasGuardadasUsuario" (
    "Id" uuid NOT NULL,
    "UsuarioId" uuid NOT NULL,
    "ImagenUrl" character varying(500) NOT NULL,
    "ActualizadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."FirmasGuardadasUsuario" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."Gestiones" (
    "Id" uuid NOT NULL,
    "TrabajadorId" uuid NOT NULL,
    "CentroId" uuid NOT NULL,
    "TipoDocumentoId" uuid NOT NULL,
    "Estado" integer NOT NULL,
    "Notas" character varying(1000),
    "MensajeOrigenId" uuid,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid
);

ALTER TABLE ONLY public."Gestiones" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."HistorialImportaciones" (
    "Id" uuid NOT NULL,
    "Plantilla" character varying(50) NOT NULL,
    "NombreArchivo" character varying(260) NOT NULL,
    "EjecutadaEnUtc" timestamp with time zone NOT NULL,
    "EjecutadaPorUsuarioId" uuid NOT NULL,
    "Exitosa" boolean NOT NULL,
    "TotalCreados" integer NOT NULL,
    "TotalAdvertencias" integer NOT NULL,
    "TotalOmitidos" integer NOT NULL,
    "MensajeError" character varying(500),
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."HistorialImportaciones" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."HistorialInformes" (
    "Id" uuid NOT NULL,
    "TipoInforme" character varying(50) NOT NULL,
    "ClienteId" uuid,
    "ClienteNombre" character varying(200),
    "GeneradoEnUtc" timestamp with time zone NOT NULL,
    "GeneradoPorUsuarioId" uuid NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."HistorialInformes" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."Incidencias" (
    "Id" uuid NOT NULL,
    "CentroId" uuid NOT NULL,
    "TrabajadorId" uuid,
    "Tipo" text NOT NULL,
    "Gravedad" text NOT NULL,
    "FechaOcurrencia" date NOT NULL,
    "Descripcion" character varying(2000) NOT NULL,
    "Resuelta" boolean NOT NULL,
    "ResueltaEnUtc" timestamp with time zone,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid
);

ALTER TABLE ONLY public."Incidencias" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."IncidenciasPurga" (
    "Id" uuid NOT NULL,
    "SolicitudPurgaId" uuid NOT NULL,
    "ObjetivoId" uuid NOT NULL,
    "Tipo" character varying(40) NOT NULL,
    "Detalle" character varying(500) NOT NULL,
    "DetectadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."IncidenciasPurga" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."InstruccionesTratamientoIaTenantPropietario" (
    "Id" uuid NOT NULL,
    "VersionDpaAceptada" character varying(20) CONSTRAINT "InstruccionesTratamientoIaTenantPro_VersionDpaAceptada_not_null" NOT NULL,
    "VersionAnexoSubencargadosAceptada" character varying(20) CONSTRAINT "InstruccionesTratamientoIaT_VersionAnexoSubencargadosA_not_null" NOT NULL,
    "FechaAceptacionUtc" timestamp with time zone CONSTRAINT "InstruccionesTratamientoIaTenantPro_FechaAceptacionUtc_not_null" NOT NULL,
    "OrigenInstruccion" character varying(30) CONSTRAINT "InstruccionesTratamientoIaTenantProp_OrigenInstruccion_not_null" NOT NULL,
    "RegistradaPorUsuarioId" uuid CONSTRAINT "InstruccionesTratamientoIaTenan_RegistradaPorUsuarioId_not_null" NOT NULL,
    "RevocadaEnUtc" timestamp with time zone,
    "MotivoRevocacion" character varying(500),
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."InstruccionesTratamientoIaTenantPropietario" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ItemsGeneracionDocumento" (
    "Id" uuid NOT NULL,
    "LoteGeneracionDocumentoId" uuid NOT NULL,
    "TrabajadorId" uuid NOT NULL,
    "DocumentoGeneradoId" uuid,
    "Estado" text NOT NULL,
    "Error" character varying(500),
    "TenantId" uuid NOT NULL,
    "Version" uuid DEFAULT '00000000-0000-0000-0000-000000000000'::uuid NOT NULL
);

ALTER TABLE ONLY public."ItemsGeneracionDocumento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."LineasWhatsApp" (
    "Id" uuid NOT NULL,
    "ConexionIntegracionId" uuid NOT NULL,
    "PhoneNumberId" character varying(50) NOT NULL,
    "WabaId" character varying(50) NOT NULL,
    "NumeroTelefono" character varying(20) NOT NULL,
    "TokenAcceso" text NOT NULL,
    "Modo" integer NOT NULL,
    "ComercialAsignadoId" uuid,
    "MensajeAutoTriage" character varying(1000),
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."LineasWhatsApp" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."LotesGeneracionDocumento" (
    "Id" uuid NOT NULL,
    "PlantillaDocumentoVersionId" uuid NOT NULL,
    "ContextoJson" text,
    "Estado" text NOT NULL,
    "TotalItems" integer NOT NULL,
    "ItemsCompletados" integer NOT NULL,
    "ItemsFallidos" integer NOT NULL,
    "UsuarioSolicitanteId" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "CompletadoEnUtc" timestamp with time zone,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."LotesGeneracionDocumento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."MacrosRespuesta" (
    "Id" uuid NOT NULL,
    "ClienteId" uuid,
    "Titulo" character varying(150) NOT NULL,
    "CuerpoHtml" text NOT NULL,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid
);

ALTER TABLE ONLY public."MacrosRespuesta" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."Mensajes" (
    "Id" uuid CONSTRAINT "MensajesCorreo_Id_not_null" NOT NULL,
    "ConversacionId" uuid CONSTRAINT "MensajesCorreo_ConversacionCorreoId_not_null" NOT NULL,
    "Direccion" integer CONSTRAINT "MensajesCorreo_Direccion_not_null" NOT NULL,
    "Remitente" character varying(320) CONSTRAINT "MensajesCorreo_RemitenteEmail_not_null" NOT NULL,
    "CuerpoHtml" text CONSTRAINT "MensajesCorreo_CuerpoHtml_not_null" NOT NULL,
    "FechaUtc" timestamp with time zone CONSTRAINT "MensajesCorreo_FechaUtc_not_null" NOT NULL,
    "TenantId" uuid CONSTRAINT "MensajesCorreo_TenantId_not_null" NOT NULL,
    "MensajeExternoId" character varying(300),
    "ErrorEntrega" character varying(500),
    "EstadoEntrega" integer,
    "Canal" integer NOT NULL
);

ALTER TABLE ONLY public."Mensajes" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."MiembrosPoolLinea" (
    "Id" uuid NOT NULL,
    "LineaWhatsAppId" uuid NOT NULL,
    "UsuarioId" uuid NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."MiembrosPoolLinea" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."NotasInternasConversacion" (
    "Id" uuid NOT NULL,
    "ConversacionId" uuid NOT NULL,
    "AutorUsuarioId" uuid NOT NULL,
    "Texto" character varying(4000) NOT NULL,
    "FechaUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."NotasInternasConversacion" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."NotificacionesUsuario" (
    "Id" uuid NOT NULL,
    "UsuarioDestinatarioId" uuid NOT NULL,
    "Titulo" character varying(200) NOT NULL,
    "Mensaje" character varying(1000) NOT NULL,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "Leida" boolean NOT NULL,
    "UrlAccion" text,
    "TextoAccion" text,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."NotificacionesUsuario" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."OperacionesImportacion" (
    "Id" uuid NOT NULL,
    "OperacionId" uuid NOT NULL,
    "ConfirmadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."OperacionesImportacion" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."OrdenMenuLateral" (
    "Id" uuid NOT NULL,
    "OrdenGrupos" text[] NOT NULL,
    "OrdenEnlaces" text[] NOT NULL,
    "ActualizadoPorUsuarioId" uuid NOT NULL,
    "ActualizadoEnUtc" timestamp with time zone NOT NULL,
    "Version" uuid NOT NULL,
    CONSTRAINT "CK_OrdenMenuLateral_FilaUnica" CHECK (("Id" = '0dde0000-0000-4000-8000-00000000e4a1'::uuid))
);

ALTER TABLE ONLY public."OrdenMenuLateral" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ParametrosSistema" (
    "Id" uuid NOT NULL,
    "UmbralAmbarDias" integer NOT NULL,
    "UmbralRojoDias" integer NOT NULL,
    "TenantId" uuid NOT NULL,
    "HorasAvisoVisita" integer DEFAULT 0 NOT NULL,
    "HorasCriticasVisita" integer DEFAULT 0 NOT NULL,
    "ExcluirFueraDeJornadaEnMetricas" boolean DEFAULT true NOT NULL,
    "HoraFinJornada" time without time zone DEFAULT '18:00:00'::time without time zone NOT NULL,
    "HoraInicioJornada" time without time zone DEFAULT '08:00:00'::time without time zone NOT NULL,
    "HorasJornadaMensualGestor" integer DEFAULT 160 NOT NULL,
    "MedicionTiempoActiva" boolean DEFAULT false NOT NULL,
    "SegundosInactividadPausa" integer DEFAULT 120 NOT NULL,
    "PresupuestoMensualIaUsd" numeric
);

ALTER TABLE ONLY public."ParametrosSistema" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ParticipantesConversacion" (
    "Id" uuid NOT NULL,
    "ConversacionId" uuid CONSTRAINT "ParticipantesConversacion_ConversacionCorreoId_not_null" NOT NULL,
    "Email" character varying(320) NOT NULL,
    "Rol" integer NOT NULL,
    "TipoOrigen" integer NOT NULL,
    "EntidadRelacionadaId" uuid,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."ParticipantesConversacion" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."PasosTareaAsistente" (
    "Id" uuid NOT NULL,
    "TareaAsistenteId" uuid NOT NULL,
    "Posicion" integer NOT NULL,
    "OrdenAsistenteId" character varying(64) NOT NULL,
    "DatosJson" character varying(16000) NOT NULL,
    "Resumen" character varying(500),
    "CamposPendientesJson" text NOT NULL,
    "AvisosJson" text NOT NULL,
    "AsistidoPorIa" boolean NOT NULL,
    "Estado" character varying(32) NOT NULL,
    "ConfirmadoEnUtc" timestamp with time zone,
    "EjecutadoEnUtc" timestamp with time zone,
    "EntidadResultadoId" uuid,
    "MotivoFallo" character varying(1000),
    "ActualizadoEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    CONSTRAINT "CK_PasosTareaAsistente_EjecucionTrasConfirmacion" CHECK ((("Estado" NOT IN ('Confirmado', 'Ejecutado', 'Fallido')) OR ("ConfirmadoEnUtc" IS NOT NULL))),
    CONSTRAINT "CK_PasosTareaAsistente_EjecutadoConFecha" CHECK (((("Estado")::text <> 'Ejecutado'::text) OR ("EjecutadoEnUtc" IS NOT NULL)))
);

ALTER TABLE ONLY public."PasosTareaAsistente" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."PlantillasDocumento" (
    "Id" uuid NOT NULL,
    "Origen" text NOT NULL,
    "Nombre" character varying(200) NOT NULL,
    "Descripcion" character varying(1000),
    "AmbitoAplicacion" text NOT NULL,
    "CentroId" uuid,
    "ClienteId" uuid,
    "CodigoFormato" character varying(20),
    "FormatoOrigen" text NOT NULL,
    "Estado" text NOT NULL,
    "VersionActualId" uuid,
    "TenantId" uuid NOT NULL,
    "TipoDocumentoId" uuid DEFAULT '00000000-0000-0000-0000-000000000000'::uuid NOT NULL
);

ALTER TABLE ONLY public."PlantillasDocumento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."PlantillasDocumentoVersion" (
    "Id" uuid NOT NULL,
    "PlantillaDocumentoId" uuid NOT NULL,
    "NumeroVersion" integer NOT NULL,
    "ArchivoOriginalUrl" character varying(500),
    "HashSha256ArchivoOriginal" character varying(64),
    "EstadoConfiguracion" text NOT NULL,
    "ConfirmadaPorUsuarioId" uuid,
    "ConfirmadaEnUtc" timestamp with time zone,
    "VigenciaLegalHasta" date,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."PlantillasDocumentoVersion" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."PlantillasElemento" (
    "Id" uuid NOT NULL,
    "PlantillaDocumentoVersionId" uuid NOT NULL,
    "Tipo" text NOT NULL,
    "Pagina" integer NOT NULL,
    "X" double precision NOT NULL,
    "Y" double precision NOT NULL,
    "Ancho" double precision NOT NULL,
    "Alto" double precision NOT NULL,
    "EtiquetaVisible" character varying(200) NOT NULL,
    "FuenteDato" text,
    "ValorConstante" character varying(500),
    "Formato" character varying(50),
    "Obligatorio" boolean NOT NULL,
    "RolFirmante" text,
    "TenantId" uuid NOT NULL,
    "NombreCampoAcroForm" character varying(200)
);

ALTER TABLE ONLY public."PlantillasElemento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."PreferenciasDashboardUsuario" (
    "Id" uuid NOT NULL,
    "UsuarioId" uuid NOT NULL,
    "CodigosKpiSeleccionados" text[] NOT NULL
);

CREATE TABLE public."ProveedoresPlataformaCae" (
    "Id" uuid NOT NULL,
    "Codigo" character varying(50) NOT NULL,
    "Nombre" character varying(150) NOT NULL,
    "Grupo" character varying(150),
    "Activo" boolean NOT NULL
);

CREATE TABLE public."Proyectos" (
    "Id" uuid NOT NULL,
    "ClienteId" uuid NOT NULL,
    "CentroId" uuid NOT NULL,
    "Nombre" character varying(200) NOT NULL,
    "FechaInicio" date NOT NULL,
    "FechaFinPrevista" date,
    "FechaCierreReal" date,
    "Notas" character varying(1000),
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid
);

ALTER TABLE ONLY public."Proyectos" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ProyectosTecnicos" (
    "Id" uuid NOT NULL,
    "ProyectoId" uuid NOT NULL,
    "TrabajadorId" uuid NOT NULL,
    "FechaAlta" date NOT NULL,
    "FechaBaja" date,
    "TenantId" uuid NOT NULL,
    "Version" uuid DEFAULT '00000000-0000-0000-0000-000000000000'::uuid NOT NULL
);

ALTER TABLE ONLY public."ProyectosTecnicos" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."RechazosAcreditacionDocumentoPlataforma" (
    "Id" uuid NOT NULL,
    "AcreditacionId" uuid NOT NULL,
    "Causa" integer NOT NULL,
    "MotivoLiteral" character varying(1000) NOT NULL,
    "FechaUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."RechazosAcreditacionDocumentoPlataforma" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ReclamacionesBuzonIntegracion" (
    "Id" uuid NOT NULL,
    "BuzonEmail" character varying(320) NOT NULL,
    "TenantPropietarioId" uuid NOT NULL,
    "ConexionIntegracionId" uuid NOT NULL,
    "ReclamadoEnUtc" timestamp with time zone NOT NULL
);

CREATE TABLE public."ReclamacionesDocumentales" (
    "Id" uuid NOT NULL,
    "ClienteId" uuid,
    "EnviadoPorUsuarioId" uuid NOT NULL,
    "DestinatarioEmail" character varying(500) NOT NULL,
    "FechaEnvioUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid,
    "ConversacionId" uuid,
    "EmpresaId" uuid,
    CONSTRAINT "CK_ReclamacionesDocumentales_TitularUnico" CHECK ((num_nonnulls("ClienteId", "EmpresaId") = 1))
);

ALTER TABLE ONLY public."ReclamacionesDocumentales" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."ReclamacionesDocumentalesDocumentos" (
    "Id" uuid NOT NULL,
    "ReclamacionDocumentalId" uuid CONSTRAINT "ReclamacionesDocumentalesDocum_ReclamacionDocumentalId_not_null" NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."ReclamacionesDocumentalesDocumentos" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."RegistrosAccesoDocumentoSensible" (
    "Id" uuid NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "Sensibilidad" character varying(30) NOT NULL,
    "TipoAcceso" character varying(20) NOT NULL,
    "UsuarioId" uuid,
    "ActorRealUsuarioId" uuid,
    "ViaAcceso" character varying(30) NOT NULL,
    "ViaAccesoId" uuid,
    "OcurridoEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "TipoActor" character varying(30) DEFAULT 'Desconocido'::character varying NOT NULL
);

ALTER TABLE ONLY public."RegistrosAccesoDocumentoSensible" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."RegistrosActividadSoporte" (
    "Id" uuid NOT NULL,
    "UsuarioSoporteId" uuid NOT NULL,
    "Tipo" character varying(30) NOT NULL,
    "DelegacionTenantId" uuid,
    "Detalle" character varying(500),
    "OcurridaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "SesionPrivilegiadaId" uuid,
    CONSTRAINT "CK_RegistrosActividadSoporte_UnSoloAgrupador" CHECK ((("DelegacionTenantId" IS NULL) <> ("SesionPrivilegiadaId" IS NULL)))
);

ALTER TABLE ONLY public."RegistrosActividadSoporte" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."RegistrosAuditoria" (
    "Id" uuid NOT NULL,
    "EntidadTipo" character varying(200) NOT NULL,
    "EntidadId" uuid NOT NULL,
    "Accion" character varying(20) NOT NULL,
    "DatosAntes" text,
    "DatosDespues" text,
    "UsuarioId" uuid,
    "FechaUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "ActorRealUsuarioId" uuid,
    "ViaAcceso" character varying(20),
    "ViaAccesoId" uuid,
    "TipoActor" character varying(30) DEFAULT 'Desconocido'::character varying NOT NULL
);

ALTER TABLE ONLY public."RegistrosAuditoria" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."RegistrosTiempoGestion" (
    "Id" uuid NOT NULL,
    "ConversacionId" uuid NOT NULL,
    "UsuarioId" uuid NOT NULL,
    "ClienteId" uuid,
    "InicioUtc" timestamp with time zone NOT NULL,
    "FinUtc" timestamp with time zone NOT NULL,
    "SegundosActivos" integer NOT NULL,
    "Motivo" integer NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."RegistrosTiempoGestion" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."RelacionesEmpresariales" (
    "Id" uuid NOT NULL,
    "ProveedoraId" uuid NOT NULL,
    "ClienteId" uuid NOT NULL,
    "EnmarcadaEnId" uuid,
    "VigenciaDesde" timestamp with time zone NOT NULL,
    "VigenciaHasta" timestamp with time zone,
    "OrigenVigencia" character varying(30) NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    CONSTRAINT "CK_RelacionesEmpresariales_NoAutorreferencia" CHECK (("ProveedoraId" <> "ClienteId")),
    CONSTRAINT "CK_RelacionesEmpresariales_NoEnmarcadaEnSiMisma" CHECK (("EnmarcadaEnId" IS DISTINCT FROM "Id")),
    CONSTRAINT "CK_RelacionesEmpresariales_VigenciaOrdenada" CHECK ((("VigenciaHasta" IS NULL) OR ("VigenciaHasta" >= "VigenciaDesde")))
);

ALTER TABLE ONLY public."RelacionesEmpresariales" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."RevisionesIaDocumento" (
    "Id" uuid NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "ConfianzaGeneral" integer NOT NULL,
    "TipoDetectado" character varying(150),
    "FechaEmisionDetectada" date,
    "FechaVencimientoDetectada" date,
    "TieneFirmaDetectada" boolean,
    "Motivo" character varying(500) NOT NULL,
    "Resuelta" boolean NOT NULL,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "AuditoriaExtraccionIaId" uuid
);

ALTER TABLE ONLY public."RevisionesIaDocumento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."SellosEmpresa" (
    "Id" uuid NOT NULL,
    "EmpresaId" uuid NOT NULL,
    "ImagenUrl" character varying(500) NOT NULL,
    "ActualizadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."SellosEmpresa" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."SesionesPrivilegiadas" (
    "Id" uuid NOT NULL,
    "ConcesionPrivilegioId" uuid NOT NULL,
    "TenantObjetivoId" uuid NOT NULL,
    "UsuarioSimuladoId" uuid,
    "Motivo" character varying(500) NOT NULL,
    "Ticket" character varying(100),
    "InicioEnUtc" timestamp with time zone NOT NULL,
    "ExpiraEnUtc" timestamp with time zone NOT NULL,
    "CerradaEnUtc" timestamp with time zone,
    "Version" uuid NOT NULL,
    "Capacidad" character varying(30) NOT NULL
);

ALTER TABLE ONLY public."SesionesPrivilegiadas" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."SolicitudesCertificacionTgss" (
    "Id" uuid NOT NULL,
    "EmpresaId" uuid NOT NULL,
    "ClienteId" uuid NOT NULL,
    "FechaSolicitud" date NOT NULL,
    "SolicitadaPorUsuarioId" uuid NOT NULL,
    "Resultado" integer,
    "FechaRespuesta" date,
    "RespuestaRegistradaPorUsuarioId" uuid,
    "EvidenciaArchivoRuta" text,
    "EvidenciaNombreArchivo" character varying(260),
    "Observaciones" character varying(1000),
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid
);

ALTER TABLE ONLY public."SolicitudesCertificacionTgss" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."SolicitudesConexionMicrosoft365" (
    "Id" uuid NOT NULL,
    "UsuarioSolicitanteId" uuid NOT NULL,
    "ClienteId" uuid,
    "GestorPropietarioId" uuid,
    "FechaExpiracionUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."SolicitudesConexionMicrosoft365" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."SolicitudesIncorporacionCartera" (
    "Id" uuid NOT NULL,
    "OperadorTenantId" uuid NOT NULL,
    "PropietarioTenantId" uuid NOT NULL,
    "AsignacionOperacionId" uuid NOT NULL,
    "SolicitanteUsuarioId" uuid NOT NULL,
    "Mensaje" character varying(1000) NOT NULL,
    "Estado" character varying(20) NOT NULL,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "ResueltaPorUsuarioId" uuid,
    "ResueltaEnUtc" timestamp with time zone,
    "MotivoAnulacion" character varying(30),
    "AsignacionCarteraId" uuid,
    "AsignacionOperadorDelegadoId" uuid,
    "RevocadaPorUsuarioId" uuid,
    "RevocadaEnUtc" timestamp with time zone,
    "Version" uuid NOT NULL
);

CREATE TABLE public."SolicitudesPrioridadDocumento" (
    "Id" uuid NOT NULL,
    "CentroId" uuid NOT NULL,
    "EnviadaPorUsuarioId" uuid NOT NULL,
    "EnviadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."SolicitudesPrioridadDocumento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."SolicitudesPurga" (
    "Id" uuid NOT NULL,
    "TipoDato" character varying(40) NOT NULL,
    "RegistrosAfectados" integer NOT NULL,
    "FechaCorte" date NOT NULL,
    "Estado" character varying(30) NOT NULL,
    "DetectadaEnUtc" timestamp with time zone NOT NULL,
    "AvisadaAlTenantEnUtc" timestamp with time zone,
    "FechaEjecucionProgramada" date,
    "AutorizadaPorUsuarioId" uuid,
    "EjecutadaEnUtc" timestamp with time zone,
    "Motivo" character varying(500),
    "TenantId" uuid NOT NULL,
    "CandidatosEnEjecucion" integer,
    "FallidosEnEjecucion" integer,
    "ResultadoEjecucion" character varying(20),
    "SuprimidosEnEjecucion" integer
);

ALTER TABLE ONLY public."SolicitudesPurga" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."SugerenciasGestionCorreo" (
    "Id" uuid NOT NULL,
    "MensajeId" uuid CONSTRAINT "SugerenciasGestionCorreo_MensajeCorreoId_not_null" NOT NULL,
    "Resumen" character varying(500) NOT NULL,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "Confianza" integer DEFAULT 0 NOT NULL
);

ALTER TABLE ONLY public."SugerenciasGestionCorreo" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."SugerenciasVisitaCorreo" (
    "Id" uuid NOT NULL,
    "MensajeId" uuid CONSTRAINT "SugerenciasVisitaCorreo_MensajeCorreoId_not_null" NOT NULL,
    "CentroId" uuid,
    "FechaInicioSugerida" date,
    "FechaFinSugerida" date,
    "Resumen" character varying(500) NOT NULL,
    "Resuelta" boolean NOT NULL,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL,
    "Confianza" integer DEFAULT 0 NOT NULL,
    "ConfianzaCentro" integer DEFAULT 0 NOT NULL,
    "ConfianzaFechas" integer DEFAULT 0 NOT NULL,
    "Resolucion" integer,
    "ResueltaEnUtc" timestamp with time zone,
    "ResueltaPorUsuarioId" uuid
);

ALTER TABLE ONLY public."SugerenciasVisitaCorreo" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."SuscripcionesWebhook" (
    "Id" uuid NOT NULL,
    "ConexionIntegracionId" uuid NOT NULL,
    "GraphSubscriptionId" character varying(100) NOT NULL,
    "ClientState" text NOT NULL,
    "FechaExpiracionUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."SuscripcionesWebhook" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."TareasAsistente" (
    "Id" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "ActorRealUsuarioId" uuid NOT NULL,
    "UsuarioSimuladoId" uuid,
    "TenantOrigenId" uuid,
    "ViaAcceso" character varying(32) NOT NULL,
    "ViaAccesoId" uuid,
    "Estado" character varying(32) NOT NULL,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "ActualizadaEnUtc" timestamp with time zone NOT NULL,
    "PlanConfirmadoPorActorRealUsuarioId" uuid,
    "PlanConfirmadoComoUsuarioSimuladoId" uuid,
    "PlanConfirmadoEnUtc" timestamp with time zone,
    "DescartadaEnUtc" timestamp with time zone,
    "TenantId" uuid NOT NULL,
    CONSTRAINT "CK_TareasAsistente_ConfirmadaConConfirmacion" CHECK ((("Estado" NOT IN ('Confirmada', 'Terminada')) OR (("PlanConfirmadoEnUtc" IS NOT NULL) AND ("PlanConfirmadoPorActorRealUsuarioId" IS NOT NULL))))
);

ALTER TABLE ONLY public."TareasAsistente" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."TarifasCliente" (
    "Id" uuid NOT NULL,
    "ClienteId" uuid NOT NULL,
    "Concepto" integer NOT NULL,
    "PrecioUnitario" numeric(18,4) NOT NULL,
    "MonedaIso" character varying(3) NOT NULL,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid
);

ALTER TABLE ONLY public."TarifasCliente" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."Tenants" (
    "Id" uuid NOT NULL,
    "Nombre" character varying(200) NOT NULL,
    "Estado" text NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EsPlataforma" boolean NOT NULL,
    "PerfilVocabulario" character varying(20) DEFAULT 'ClienteDirecto'::character varying NOT NULL,
    "EstadoComercial" character varying(20) DEFAULT 'SinSuscripcion'::character varying NOT NULL,
    "EstadoComercialActualizadoEnUtc" timestamp with time zone,
    "StripeCustomerId" character varying(100),
    "StripeSubscriptionId" character varying(100),
    "DatosDemoCompletadosEnUtc" timestamp with time zone,
    "PuedeActuarComoOperadorCaeExterno" boolean DEFAULT false NOT NULL
);

CREATE TABLE public."TenantsAlcanzadosPorConcesion" (
    "Id" uuid NOT NULL,
    "ConcesionPrivilegioId" uuid NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."TenantsAlcanzadosPorConcesion" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."TiposDocumento" (
    "Id" uuid NOT NULL,
    "Nombre" character varying(150) NOT NULL,
    "VigenciaMeses" integer,
    "AplicaVencimientoAutomatico" boolean NOT NULL,
    "Notas" character varying(500),
    "Orden" integer NOT NULL,
    "Descripcion" character varying(1000),
    "CriteriosValidacion" character varying(1000),
    "SeSolicitaA" character varying(300),
    "Observaciones" character varying(1000),
    "AmbitoAplicacion" character varying(20) NOT NULL,
    "LecturaIaActiva" boolean NOT NULL,
    "DeteccionTrabajadoresActiva" boolean NOT NULL,
    "VerificacionIaActiva" boolean NOT NULL,
    "TenantId" uuid NOT NULL,
    "PerfilDocumentoOficial" character varying(20) DEFAULT 'Ninguno'::character varying NOT NULL,
    "Naturaleza" character varying(30) DEFAULT ''::character varying NOT NULL,
    "Requerido" character varying(20) DEFAULT ''::character varying NOT NULL,
    "Sensibilidad" character varying(30) DEFAULT 'CategoriaEspecialSalud'::character varying NOT NULL
);

ALTER TABLE ONLY public."TiposDocumento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."TiposDocumentoAlias" (
    "Id" uuid NOT NULL,
    "TipoDocumentoId" uuid NOT NULL,
    "Texto" character varying(100) NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."TiposDocumentoAlias" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."TiposDocumentoCentros" (
    "Id" uuid NOT NULL,
    "TipoDocumentoId" uuid NOT NULL,
    "CentroId" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "ArchivoUrl" character varying(500),
    "BloqueaAcceso" boolean DEFAULT false NOT NULL,
    "Incluido" boolean DEFAULT true NOT NULL,
    "NombreArchivoOriginal" character varying(260),
    "PeriodicidadEspecialMeses" integer
);

ALTER TABLE ONLY public."TiposDocumentoCentros" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."Trabajadores" (
    "Id" uuid NOT NULL,
    "EmpresaId" uuid,
    "SubcontrataId" uuid,
    "Nombre" character varying(100) NOT NULL,
    "Apellidos" character varying(150) NOT NULL,
    "Alias" character varying(150),
    "Dni" character varying(20),
    "FechaNacimiento" date,
    "Email" character varying(200),
    "Observaciones" character varying(1000),
    "AnonimizadoEnUtc" timestamp with time zone,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid,
    "Telefono" character varying(20),
    "Puesto" character varying(150),
    CONSTRAINT "CK_Trabajadores_EmpresaXorSubcontrata" CHECK ((("EmpresaId" IS NULL) <> ("SubcontrataId" IS NULL)))
);

ALTER TABLE ONLY public."Trabajadores" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."TrabajosAnalisisDocumento" (
    "Id" uuid NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "UsuarioSolicitanteId" uuid,
    "Tipo" text NOT NULL,
    "Estado" text NOT NULL,
    "Intentos" integer NOT NULL,
    "UltimoError" character varying(2000),
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "IniciadoEnUtc" timestamp with time zone,
    "CompletadoEnUtc" timestamp with time zone,
    "TenantId" uuid NOT NULL,
    "SiguienteIntentoEnUtc" timestamp with time zone,
    "MotivoDescarte" character varying(500),
    "VersionDocumentoEncolada" uuid
);

ALTER TABLE ONLY public."TrabajosAnalisisDocumento" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."TurnosTareaAsistente" (
    "Id" uuid NOT NULL,
    "TareaAsistenteId" uuid NOT NULL,
    "Numero" integer NOT NULL,
    "Autor" character varying(32) NOT NULL,
    "TextoOriginal" character varying(8000) NOT NULL,
    "TextoEnmascarado" character varying(8000),
    "FechaUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."TurnosTareaAsistente" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."UltimosResumenesNotificacionPlataforma" (
    "Id" uuid NOT NULL,
    "ClienteId" uuid NOT NULL,
    "ProveedorPlataformaCaeId" uuid CONSTRAINT "UltimosResumenesNotificacionP_ProveedorPlataformaCaeId_not_null" NOT NULL,
    "Pendientes" integer NOT NULL,
    "Vencidos" integer NOT NULL,
    "Rechazados" integer NOT NULL,
    "ActualizadoEnUtc" timestamp with time zone CONSTRAINT "UltimosResumenesNotificacionPlataform_ActualizadoEnUtc_not_null" NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."UltimosResumenesNotificacionPlataforma" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."Vehiculos" (
    "Id" uuid NOT NULL,
    "EmpresaId" uuid,
    "SubcontrataId" uuid,
    "Nombre" character varying(100) NOT NULL,
    "Modelo" character varying(100) NOT NULL,
    "NumeroPlaca" character varying(20) NOT NULL,
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid
);

ALTER TABLE ONLY public."Vehiculos" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."VerificacionesDocumentoOficial" (
    "Id" uuid NOT NULL,
    "DocumentoId" uuid NOT NULL,
    "Perfil" text NOT NULL,
    "NivelConfianza" text NOT NULL,
    "CodigoVerificacion" character varying(100),
    "CifDetectado" character varying(20),
    "RazonSocialDetectada" character varying(300),
    "FechaEmisionDetectada" date,
    "PeriodoDetectado" character varying(20),
    "ResultadoCotejo" text NOT NULL,
    "Decision" text NOT NULL,
    "Motivos" character varying(1000) NOT NULL,
    "CreadaEnUtc" timestamp with time zone NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."VerificacionesDocumentoOficial" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."VerificacionesExternaSubcontrata" (
    "Id" uuid NOT NULL,
    "SubcontrataId" uuid NOT NULL,
    "CentroId" uuid NOT NULL,
    "TipoDocumentoId" uuid NOT NULL,
    "FechaVerificacion" date NOT NULL,
    "Resultado" integer NOT NULL,
    "ValidoHasta" date,
    "EvidenciaArchivoRuta" text,
    "EvidenciaNombreArchivo" character varying(260),
    "UsuarioVerificadorId" uuid NOT NULL,
    "Observaciones" character varying(1000),
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid
);

ALTER TABLE ONLY public."VerificacionesExternaSubcontrata" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."Visitas" (
    "Id" uuid NOT NULL,
    "CentroId" uuid NOT NULL,
    "FechaInicio" date NOT NULL,
    "FechaFin" date NOT NULL,
    "NotificadoCliente" boolean NOT NULL,
    "Notas" character varying(1000),
    "TenantId" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "CreadoEnUtc" timestamp with time zone NOT NULL,
    "EstaEliminado" boolean NOT NULL,
    "EliminadoEnUtc" timestamp with time zone,
    "EliminadoPorUsuarioId" uuid,
    "Origen" integer DEFAULT 0 NOT NULL,
    "AntelacionEfectivaHoras" numeric(10,2),
    "AntelacionNominalHoras" numeric(10,2),
    "Atribucion" integer DEFAULT 0 NOT NULL,
    "ConversacionOrigenId" uuid,
    "FechaHoraExpedienteCompletoUtc" timestamp with time zone,
    "FechaHoraSolicitudUtc" timestamp with time zone,
    "HoraEstimadaAcceso" time without time zone,
    "Tramo" integer,
    "CanceladaEnUtc" timestamp with time zone,
    "EstaCancelada" boolean DEFAULT false NOT NULL,
    "MotivoCancelacion" character varying(500),
    "MotivoReactivacion" character varying(500),
    "ReactivadaEnUtc" timestamp with time zone
);

ALTER TABLE ONLY public."Visitas" FORCE ROW LEVEL SECURITY;

CREATE TABLE public."VisitasTrabajadores" (
    "Id" uuid NOT NULL,
    "VisitaId" uuid NOT NULL,
    "TrabajadorId" uuid NOT NULL,
    "TenantId" uuid NOT NULL
);

ALTER TABLE ONLY public."VisitasTrabajadores" FORCE ROW LEVEL SECURITY;

ALTER TABLE ONLY app_privado.claves_contexto
    ADD CONSTRAINT claves_contexto_pkey PRIMARY KEY (id);

ALTER TABLE ONLY public."AsignacionesOperacion"
    ADD CONSTRAINT "AK_AsignacionesOperacion_Id_PropietarioTenantId" UNIQUE ("Id", "PropietarioTenantId");

ALTER TABLE ONLY public."CanalesGestionDocumental"
    ADD CONSTRAINT "AK_CanalesGestionDocumental_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."Centros"
    ADD CONSTRAINT "AK_Centros_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."Centros"
    ADD CONSTRAINT "AK_Centros_TenantId_Id_ClienteId" UNIQUE ("TenantId", "Id", "ClienteId");

ALTER TABLE ONLY public."ConcesionesPrivilegio"
    ADD CONSTRAINT "AK_ConcesionesPrivilegio_Id_Capacidad" UNIQUE ("Id", "Capacidad");

ALTER TABLE ONLY public."ConexionesIntegracion"
    ADD CONSTRAINT "AK_ConexionesIntegracion_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."Conversaciones"
    ADD CONSTRAINT "AK_Conversaciones_Id_TenantId" UNIQUE ("Id", "TenantId");

ALTER TABLE ONLY public."Documentos"
    ADD CONSTRAINT "AK_Documentos_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."Empresas"
    ADD CONSTRAINT "AK_Empresas_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."ExtraccionesIaCache"
    ADD CONSTRAINT "AK_ExtraccionesIaCache_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."LotesGeneracionDocumento"
    ADD CONSTRAINT "AK_LotesGeneracionDocumento_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."Mensajes"
    ADD CONSTRAINT "AK_Mensajes_Id_TenantId" UNIQUE ("Id", "TenantId");

ALTER TABLE ONLY public."Proyectos"
    ADD CONSTRAINT "AK_Proyectos_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."RelacionesEmpresariales"
    ADD CONSTRAINT "AK_RelacionesEmpresariales_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."SolicitudesPurga"
    ADD CONSTRAINT "AK_SolicitudesPurga_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."TareasAsistente"
    ADD CONSTRAINT "AK_TareasAsistente_Id_TenantId" UNIQUE ("Id", "TenantId");

ALTER TABLE ONLY public."TiposDocumento"
    ADD CONSTRAINT "AK_TiposDocumento_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."Trabajadores"
    ADD CONSTRAINT "AK_Trabajadores_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."Vehiculos"
    ADD CONSTRAINT "AK_Vehiculos_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."Visitas"
    ADD CONSTRAINT "AK_Visitas_TenantId_Id" UNIQUE ("TenantId", "Id");

ALTER TABLE ONLY public."Asignaciones"
    ADD CONSTRAINT "EX_Asignaciones_SinSolapeVigencia" EXCLUDE USING gist ("TenantId" WITH =, "TrabajadorId" WITH =, "CentroId" WITH =, daterange("FechaAlta", "FechaBaja", '[)'::text) WITH &&);

ALTER TABLE ONLY public."AceptacionesTerminos"
    ADD CONSTRAINT "PK_AceptacionesTerminos" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AcreditacionesDocumentoPlataforma"
    ADD CONSTRAINT "PK_AcreditacionesDocumentoPlataforma" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AdjuntosMensaje"
    ADD CONSTRAINT "PK_AdjuntosMensaje" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AprobacionesDocumento"
    ADD CONSTRAINT "PK_AprobacionesDocumento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Asignaciones"
    ADD CONSTRAINT "PK_Asignaciones" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AsignacionesCartera"
    ADD CONSTRAINT "PK_AsignacionesCartera" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AsignacionesOperacion"
    ADD CONSTRAINT "PK_AsignacionesOperacion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AsignacionesOperadorDelegado"
    ADD CONSTRAINT "PK_AsignacionesOperadorDelegado" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AspNetRoleClaims"
    ADD CONSTRAINT "PK_AspNetRoleClaims" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AspNetRoles"
    ADD CONSTRAINT "PK_AspNetRoles" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AspNetUserClaims"
    ADD CONSTRAINT "PK_AspNetUserClaims" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AspNetUserLogins"
    ADD CONSTRAINT "PK_AspNetUserLogins" PRIMARY KEY ("LoginProvider", "ProviderKey");

ALTER TABLE ONLY public."AspNetUserRoles"
    ADD CONSTRAINT "PK_AspNetUserRoles" PRIMARY KEY ("UserId", "RoleId");

ALTER TABLE ONLY public."AspNetUserTokens"
    ADD CONSTRAINT "PK_AspNetUserTokens" PRIMARY KEY ("UserId", "LoginProvider", "Name");

ALTER TABLE ONLY public."AspNetUsers"
    ADD CONSTRAINT "PK_AspNetUsers" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AuditoriasExtraccionIa"
    ADD CONSTRAINT "PK_AuditoriasExtraccionIa" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."AvisosRevisionNormativa"
    ADD CONSTRAINT "PK_AvisosRevisionNormativa" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."CanalesGestionDocumental"
    ADD CONSTRAINT "PK_CanalesGestionDocumental" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Centros"
    ADD CONSTRAINT "PK_Centros" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ClasificacionesRelevanciaCae"
    ADD CONSTRAINT "PK_ClasificacionesRelevanciaCae" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ClasificacionesRuidoDetalleGestion"
    ADD CONSTRAINT "PK_ClasificacionesRuidoDetalleGestion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ClasificacionesRuidoMensaje"
    ADD CONSTRAINT "PK_ClasificacionesRuidoMensaje" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ClavesApi"
    ADD CONSTRAINT "PK_ClavesApi" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ConcesionesPrivilegio"
    ADD CONSTRAINT "PK_ConcesionesPrivilegio" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ConexionesIntegracion"
    ADD CONSTRAINT "PK_ConexionesIntegracion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ConfiguracionesIaDocumentoCliente"
    ADD CONSTRAINT "PK_ConfiguracionesIaDocumentoCliente" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ConocimientosDeteccionCampo"
    ADD CONSTRAINT "PK_ConocimientosDeteccionCampo" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ContactosAgenda"
    ADD CONSTRAINT "PK_ContactosAgenda" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ContactosAgendaRoles"
    ADD CONSTRAINT "PK_ContactosAgendaRoles" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ContactosAgendaTiposDocumento"
    ADD CONSTRAINT "PK_ContactosAgendaTiposDocumento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ContactosWhatsApp"
    ADD CONSTRAINT "PK_ContactosWhatsApp" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Conversaciones"
    ADD CONSTRAINT "PK_Conversaciones" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."CredencialesAccesoEmpresa"
    ADD CONSTRAINT "PK_CredencialesAccesoEmpresa" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."CredencialesAccesoSubcontrata"
    ADD CONSTRAINT "PK_CredencialesAccesoSubcontrata" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."CredencialesIntegracion"
    ADD CONSTRAINT "PK_CredencialesIntegracion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."DelegacionesTenant"
    ADD CONSTRAINT "PK_DelegacionesTenant" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."DetallesSugerenciaGestionCorreo"
    ADD CONSTRAINT "PK_DetallesSugerenciaGestionCorreo" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."DeteccionesTrabajador"
    ADD CONSTRAINT "PK_DeteccionesTrabajador" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Documentos"
    ADD CONSTRAINT "PK_Documentos" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."DocumentosGenerados"
    ADD CONSTRAINT "PK_DocumentosGenerados" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."DominiosProveedorPlataformaCae"
    ADD CONSTRAINT "PK_DominiosProveedorPlataformaCae" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Empresas"
    ADD CONSTRAINT "PK_Empresas" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."EstadoBootstrapPlataforma"
    ADD CONSTRAINT "PK_EstadoBootstrapPlataforma" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."EstadosAutomatizacion"
    ADD CONSTRAINT "PK_EstadosAutomatizacion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."EventosConversacion"
    ADD CONSTRAINT "PK_EventosConversacion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."EventosRecientesUsuario"
    ADD CONSTRAINT "PK_EventosRecientesUsuario" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."EventosWebhook"
    ADD CONSTRAINT "PK_EventosWebhook" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ExtraccionesIaCache"
    ADD CONSTRAINT "PK_ExtraccionesIaCache" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ExtraccionesIaCacheDocumentos"
    ADD CONSTRAINT "PK_ExtraccionesIaCacheDocumentos" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."FiltrosGuardados"
    ADD CONSTRAINT "PK_FiltrosGuardados" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."FirmasDigitalesDocumento"
    ADD CONSTRAINT "PK_FirmasDigitalesDocumento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."FirmasEnCampoDocumento"
    ADD CONSTRAINT "PK_FirmasEnCampoDocumento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."FirmasGuardadasUsuario"
    ADD CONSTRAINT "PK_FirmasGuardadasUsuario" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Gestiones"
    ADD CONSTRAINT "PK_Gestiones" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."HistorialImportaciones"
    ADD CONSTRAINT "PK_HistorialImportaciones" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."HistorialInformes"
    ADD CONSTRAINT "PK_HistorialInformes" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Incidencias"
    ADD CONSTRAINT "PK_Incidencias" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."IncidenciasPurga"
    ADD CONSTRAINT "PK_IncidenciasPurga" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."InstruccionesTratamientoIaTenantPropietario"
    ADD CONSTRAINT "PK_InstruccionesTratamientoIaTenantPropietario" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ItemsGeneracionDocumento"
    ADD CONSTRAINT "PK_ItemsGeneracionDocumento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."LineasWhatsApp"
    ADD CONSTRAINT "PK_LineasWhatsApp" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."LotesGeneracionDocumento"
    ADD CONSTRAINT "PK_LotesGeneracionDocumento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."MacrosRespuesta"
    ADD CONSTRAINT "PK_MacrosRespuesta" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Mensajes"
    ADD CONSTRAINT "PK_Mensajes" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."MiembrosPoolLinea"
    ADD CONSTRAINT "PK_MiembrosPoolLinea" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."NotasInternasConversacion"
    ADD CONSTRAINT "PK_NotasInternasConversacion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."NotificacionesUsuario"
    ADD CONSTRAINT "PK_NotificacionesUsuario" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."OperacionesImportacion"
    ADD CONSTRAINT "PK_OperacionesImportacion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."OrdenMenuLateral"
    ADD CONSTRAINT "PK_OrdenMenuLateral" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ParametrosSistema"
    ADD CONSTRAINT "PK_ParametrosSistema" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ParticipantesConversacion"
    ADD CONSTRAINT "PK_ParticipantesConversacion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."PasosTareaAsistente"
    ADD CONSTRAINT "PK_PasosTareaAsistente" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."PlantillasDocumento"
    ADD CONSTRAINT "PK_PlantillasDocumento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."PlantillasDocumentoVersion"
    ADD CONSTRAINT "PK_PlantillasDocumentoVersion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."PlantillasElemento"
    ADD CONSTRAINT "PK_PlantillasElemento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."PreferenciasDashboardUsuario"
    ADD CONSTRAINT "PK_PreferenciasDashboardUsuario" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ProveedoresPlataformaCae"
    ADD CONSTRAINT "PK_ProveedoresPlataformaCae" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Proyectos"
    ADD CONSTRAINT "PK_Proyectos" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ProyectosTecnicos"
    ADD CONSTRAINT "PK_ProyectosTecnicos" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."RechazosAcreditacionDocumentoPlataforma"
    ADD CONSTRAINT "PK_RechazosAcreditacionDocumentoPlataforma" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ReclamacionesBuzonIntegracion"
    ADD CONSTRAINT "PK_ReclamacionesBuzonIntegracion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ReclamacionesDocumentales"
    ADD CONSTRAINT "PK_ReclamacionesDocumentales" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."ReclamacionesDocumentalesDocumentos"
    ADD CONSTRAINT "PK_ReclamacionesDocumentalesDocumentos" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."RegistrosAccesoDocumentoSensible"
    ADD CONSTRAINT "PK_RegistrosAccesoDocumentoSensible" PRIMARY KEY ("Id", "OcurridoEnUtc");

ALTER TABLE ONLY public."RegistrosActividadSoporte"
    ADD CONSTRAINT "PK_RegistrosActividadSoporte" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."RegistrosAuditoria"
    ADD CONSTRAINT "PK_RegistrosAuditoria" PRIMARY KEY ("Id", "FechaUtc");

ALTER TABLE ONLY public."RegistrosTiempoGestion"
    ADD CONSTRAINT "PK_RegistrosTiempoGestion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."RelacionesEmpresariales"
    ADD CONSTRAINT "PK_RelacionesEmpresariales" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."RevisionesIaDocumento"
    ADD CONSTRAINT "PK_RevisionesIaDocumento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."SellosEmpresa"
    ADD CONSTRAINT "PK_SellosEmpresa" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."SesionesPrivilegiadas"
    ADD CONSTRAINT "PK_SesionesPrivilegiadas" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."SolicitudesCertificacionTgss"
    ADD CONSTRAINT "PK_SolicitudesCertificacionTgss" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."SolicitudesConexionMicrosoft365"
    ADD CONSTRAINT "PK_SolicitudesConexionMicrosoft365" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."SolicitudesIncorporacionCartera"
    ADD CONSTRAINT "PK_SolicitudesIncorporacionCartera" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."SolicitudesPrioridadDocumento"
    ADD CONSTRAINT "PK_SolicitudesPrioridadDocumento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."SolicitudesPurga"
    ADD CONSTRAINT "PK_SolicitudesPurga" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."SugerenciasGestionCorreo"
    ADD CONSTRAINT "PK_SugerenciasGestionCorreo" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."SugerenciasVisitaCorreo"
    ADD CONSTRAINT "PK_SugerenciasVisitaCorreo" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."SuscripcionesWebhook"
    ADD CONSTRAINT "PK_SuscripcionesWebhook" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."TareasAsistente"
    ADD CONSTRAINT "PK_TareasAsistente" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."TarifasCliente"
    ADD CONSTRAINT "PK_TarifasCliente" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Tenants"
    ADD CONSTRAINT "PK_Tenants" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."TenantsAlcanzadosPorConcesion"
    ADD CONSTRAINT "PK_TenantsAlcanzadosPorConcesion" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."TiposDocumento"
    ADD CONSTRAINT "PK_TiposDocumento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."TiposDocumentoAlias"
    ADD CONSTRAINT "PK_TiposDocumentoAlias" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."TiposDocumentoCentros"
    ADD CONSTRAINT "PK_TiposDocumentoCentros" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Trabajadores"
    ADD CONSTRAINT "PK_Trabajadores" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."TrabajosAnalisisDocumento"
    ADD CONSTRAINT "PK_TrabajosAnalisisDocumento" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."TurnosTareaAsistente"
    ADD CONSTRAINT "PK_TurnosTareaAsistente" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."UltimosResumenesNotificacionPlataforma"
    ADD CONSTRAINT "PK_UltimosResumenesNotificacionPlataforma" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Vehiculos"
    ADD CONSTRAINT "PK_Vehiculos" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."VerificacionesDocumentoOficial"
    ADD CONSTRAINT "PK_VerificacionesDocumentoOficial" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."VerificacionesExternaSubcontrata"
    ADD CONSTRAINT "PK_VerificacionesExternaSubcontrata" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."Visitas"
    ADD CONSTRAINT "PK_Visitas" PRIMARY KEY ("Id");

ALTER TABLE ONLY public."VisitasTrabajadores"
    ADD CONSTRAINT "PK_VisitasTrabajadores" PRIMARY KEY ("Id");

CREATE INDEX "EmailIndex" ON public."AspNetUsers" USING btree ("NormalizedEmail");

CREATE INDEX "IX_AceptacionesTerminos_UsuarioId_VersionDocumento" ON public."AceptacionesTerminos" USING btree ("UsuarioId", "VersionDocumento");

CREATE INDEX "IX_AcreditacionesDocumentoPlataforma_TenantId_CanalGestionDocu~" ON public."AcreditacionesDocumentoPlataforma" USING btree ("TenantId", "CanalGestionDocumentalId");

CREATE INDEX "IX_AcreditacionesDocumentoPlataforma_TenantId_DocumentoId" ON public."AcreditacionesDocumentoPlataforma" USING btree ("TenantId", "DocumentoId");

CREATE UNIQUE INDEX "IX_AcreditacionesDocumentoPlataforma_TenantId_DocumentoId_Cana~" ON public."AcreditacionesDocumentoPlataforma" USING btree ("TenantId", "DocumentoId", "CanalGestionDocumentalId");

CREATE INDEX "IX_AdjuntosMensaje_MensajeId" ON public."AdjuntosMensaje" USING btree ("MensajeId");

CREATE INDEX "IX_AsignacionesCartera_AsignacionOperacionId_PropietarioTenant~" ON public."AsignacionesCartera" USING btree ("AsignacionOperacionId", "PropietarioTenantId");

CREATE INDEX "IX_AsignacionesCartera_PropietarioTenantId_AmbitoCentroId" ON public."AsignacionesCartera" USING btree ("PropietarioTenantId", "AmbitoCentroId");

CREATE INDEX "IX_AsignacionesCartera_PropietarioTenantId_AmbitoProyectoId" ON public."AsignacionesCartera" USING btree ("PropietarioTenantId", "AmbitoProyectoId");

CREATE INDEX "IX_AsignacionesCartera_PropietarioTenantId_AmbitoTrabajadorId" ON public."AsignacionesCartera" USING btree ("PropietarioTenantId", "AmbitoTrabajadorId");

CREATE UNIQUE INDEX "IX_AsignacionesCartera_ResponsableRelacionVigente" ON public."AsignacionesCartera" USING btree ("PropietarioTenantId", "AmbitoRelacionClienteId") WHERE ((("Estado")::text = 'Vigente'::text) AND ("AmbitoRelacionClienteId" IS NOT NULL) AND ("AmbitoCentroId" IS NULL) AND ("AmbitoTrabajadorId" IS NULL) AND ("AmbitoProyectoId" IS NULL));

CREATE INDEX "IX_AsignacionesCartera_UsuarioId_Estado" ON public."AsignacionesCartera" USING btree ("UsuarioId", "Estado");

CREATE UNIQUE INDEX "IX_AsignacionesCartera_UsuarioUniversalVigente" ON public."AsignacionesCartera" USING btree ("AsignacionOperacionId", "UsuarioId") WHERE ((("Estado")::text = 'Vigente'::text) AND ("AmbitoRelacionClienteId" IS NULL) AND ("AmbitoCentroId" IS NULL) AND ("AmbitoTrabajadorId" IS NULL) AND ("AmbitoProyectoId" IS NULL));

CREATE INDEX "IX_AsignacionesOperacion_AmbitoRelacionCliente" ON public."AsignacionesOperacion" USING btree ("PropietarioTenantId", "AmbitoRelacionClienteId") WHERE ("AmbitoRelacionClienteId" IS NOT NULL);

CREATE UNIQUE INDEX "IX_AsignacionesOperacion_DelegacionTotalVigente" ON public."AsignacionesOperacion" USING btree ("PropietarioTenantId", "Servicio") WHERE ((NOT "EsRaiz") AND (("Estado")::text = 'Vigente'::text) AND ("AmbitoRelacionClienteId" IS NULL) AND ("AmbitoCentroId" IS NULL) AND ("AmbitoTrabajadorId" IS NULL) AND ("AmbitoProyectoId" IS NULL));

CREATE INDEX "IX_AsignacionesOperacion_OperadorTenantId_Estado" ON public."AsignacionesOperacion" USING btree ("OperadorTenantId", "Estado");

CREATE INDEX "IX_AsignacionesOperacion_PropietarioTenantId_AmbitoCentroId" ON public."AsignacionesOperacion" USING btree ("PropietarioTenantId", "AmbitoCentroId");

CREATE INDEX "IX_AsignacionesOperacion_PropietarioTenantId_AmbitoProyectoId" ON public."AsignacionesOperacion" USING btree ("PropietarioTenantId", "AmbitoProyectoId");

CREATE INDEX "IX_AsignacionesOperacion_PropietarioTenantId_AmbitoTrabajadorId" ON public."AsignacionesOperacion" USING btree ("PropietarioTenantId", "AmbitoTrabajadorId");

CREATE INDEX "IX_AsignacionesOperacion_PropietarioTenantId_Servicio_Estado" ON public."AsignacionesOperacion" USING btree ("PropietarioTenantId", "Servicio", "Estado");

CREATE UNIQUE INDEX "IX_AsignacionesOperacion_RaizVigente" ON public."AsignacionesOperacion" USING btree ("PropietarioTenantId", "Servicio") WHERE ("EsRaiz" AND (("Estado")::text = 'Vigente'::text));

CREATE UNIQUE INDEX "IX_AsignacionesOperacion_ResponsableRelacionVigente" ON public."AsignacionesOperacion" USING btree ("PropietarioTenantId", "Servicio", "AmbitoRelacionClienteId") WHERE ((("Estado")::text = 'Vigente'::text) AND ("AmbitoRelacionClienteId" IS NOT NULL) AND ("AmbitoCentroId" IS NULL) AND ("AmbitoTrabajadorId" IS NULL) AND ("AmbitoProyectoId" IS NULL));

CREATE UNIQUE INDEX "IX_AsignacionesOperadorDelegado_DelegacionTenantId_UsuarioId" ON public."AsignacionesOperadorDelegado" USING btree ("DelegacionTenantId", "UsuarioId") WHERE ("RevocadaEnUtc" IS NULL);

CREATE INDEX "IX_AsignacionesOperadorDelegado_UsuarioId" ON public."AsignacionesOperadorDelegado" USING btree ("UsuarioId");

CREATE INDEX "IX_Asignaciones_CentroId" ON public."Asignaciones" USING btree ("CentroId");

CREATE INDEX "IX_Asignaciones_TenantId_CentroId" ON public."Asignaciones" USING btree ("TenantId", "CentroId");

CREATE INDEX "IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa" ON public."Asignaciones" USING btree ("TenantId", "TrabajadorId", "CentroId") WHERE ("FechaBaja" IS NULL);

CREATE INDEX "IX_AspNetRoleClaims_RoleId" ON public."AspNetRoleClaims" USING btree ("RoleId");

CREATE INDEX "IX_AspNetUserClaims_UserId" ON public."AspNetUserClaims" USING btree ("UserId");

CREATE INDEX "IX_AspNetUserLogins_UserId" ON public."AspNetUserLogins" USING btree ("UserId");

CREATE INDEX "IX_AspNetUserRoles_RoleId" ON public."AspNetUserRoles" USING btree ("RoleId");

CREATE INDEX "IX_AspNetUsers_TenantId" ON public."AspNetUsers" USING btree ("TenantId");

CREATE INDEX "IX_AuditoriasExtraccionIa_DocumentoId" ON public."AuditoriasExtraccionIa" USING btree ("DocumentoId");

CREATE INDEX "IX_AuditoriasExtraccionIa_TenantId_CreadaEnUtc" ON public."AuditoriasExtraccionIa" USING btree ("TenantId", "CreadaEnUtc");

CREATE UNIQUE INDEX "IX_AvisosRevisionNormativa_IdentificadorBoe" ON public."AvisosRevisionNormativa" USING btree ("IdentificadorBoe");

CREATE INDEX "IX_AvisosRevisionNormativa_Revisado" ON public."AvisosRevisionNormativa" USING btree ("Revisado");

CREATE INDEX "IX_CanalesGestionDocumental_ProveedorPlataformaCaeId" ON public."CanalesGestionDocumental" USING btree ("ProveedorPlataformaCaeId");

CREATE INDEX "IX_CanalesGestionDocumental_TenantId_CentroId" ON public."CanalesGestionDocumental" USING btree ("TenantId", "CentroId");

CREATE UNIQUE INDEX "IX_CanalesGestionDocumental_TenantId_CentroId_Principal" ON public."CanalesGestionDocumental" USING btree ("TenantId", "CentroId") WHERE ("EsPrincipal" AND (NOT "EstaEliminado"));

CREATE INDEX "IX_Centros_ClienteId" ON public."Centros" USING btree ("ClienteId");

CREATE INDEX "IX_Centros_EmpresaId" ON public."Centros" USING btree ("EmpresaId");

CREATE INDEX "IX_Centros_Nombre_Trgm" ON public."Centros" USING gin (upper(("Nombre")::text) public.gin_trgm_ops);

CREATE INDEX "IX_Centros_TenantId_ClienteId" ON public."Centros" USING btree ("TenantId", "ClienteId");

CREATE INDEX "IX_Centros_TenantId_EmpresaId" ON public."Centros" USING btree ("TenantId", "EmpresaId");

CREATE UNIQUE INDEX "IX_Centros_TenantId_Id" ON public."Centros" USING btree ("TenantId", "Id");

CREATE UNIQUE INDEX "IX_ClasificacionesRelevanciaCae_ConversacionId" ON public."ClasificacionesRelevanciaCae" USING btree ("ConversacionId");

CREATE UNIQUE INDEX "IX_ClasificacionesRuidoDetalleGestion_DetalleSugerenciaGestion~" ON public."ClasificacionesRuidoDetalleGestion" USING btree ("DetalleSugerenciaGestionCorreoId");

CREATE UNIQUE INDEX "IX_ClasificacionesRuidoMensaje_MensajeId" ON public."ClasificacionesRuidoMensaje" USING btree ("MensajeId");

CREATE UNIQUE INDEX "IX_ClavesApi_HashClave" ON public."ClavesApi" USING btree ("HashClave");

CREATE INDEX "IX_ClavesApi_TenantId" ON public."ClavesApi" USING btree ("TenantId");

CREATE INDEX "IX_ConcesionesPrivilegio_UsuarioPlataformaId_Estado" ON public."ConcesionesPrivilegio" USING btree ("UsuarioPlataformaId", "Estado");

CREATE INDEX "IX_ConexionesIntegracion_TenantId_ClienteId" ON public."ConexionesIntegracion" USING btree ("TenantId", "ClienteId");

CREATE UNIQUE INDEX "IX_ConexionesIntegracion_TenantId_GestorPropietarioId" ON public."ConexionesIntegracion" USING btree ("TenantId", "GestorPropietarioId") WHERE (("GestorPropietarioId" IS NOT NULL) AND (NOT "EstaEliminado"));

CREATE UNIQUE INDEX "IX_ConexionesIntegracion_TenantId_Nombre" ON public."ConexionesIntegracion" USING btree ("TenantId", "Nombre");

CREATE UNIQUE INDEX "IX_ConfiguracionesIaDocumentoCliente_TenantId_ClienteId_TipoDo~" ON public."ConfiguracionesIaDocumentoCliente" USING btree ("TenantId", "ClienteId", "TipoDocumentoId");

CREATE INDEX "IX_ConocimientosDeteccionCampo_EtiquetaNormalizada" ON public."ConocimientosDeteccionCampo" USING btree ("EtiquetaNormalizada");

CREATE INDEX "IX_ContactosAgendaRoles_ContactoAgendaId" ON public."ContactosAgendaRoles" USING btree ("ContactoAgendaId");

CREATE UNIQUE INDEX "IX_ContactosAgendaRoles_TenantId_ContactoAgendaId_Rol" ON public."ContactosAgendaRoles" USING btree ("TenantId", "ContactoAgendaId", "Rol");

CREATE INDEX "IX_ContactosAgendaTiposDocumento_ContactoAgendaId" ON public."ContactosAgendaTiposDocumento" USING btree ("ContactoAgendaId");

CREATE UNIQUE INDEX "IX_ContactosAgendaTiposDocumento_TenantId_ContactoAgendaId_Tip~" ON public."ContactosAgendaTiposDocumento" USING btree ("TenantId", "ContactoAgendaId", "TipoDocumentoId");

CREATE INDEX "IX_ContactosAgendaTiposDocumento_TipoDocumentoId" ON public."ContactosAgendaTiposDocumento" USING btree ("TipoDocumentoId");

CREATE INDEX "IX_ContactosAgenda_CentroId" ON public."ContactosAgenda" USING btree ("CentroId");

CREATE INDEX "IX_ContactosAgenda_ClienteId" ON public."ContactosAgenda" USING btree ("ClienteId");

CREATE INDEX "IX_ContactosAgenda_EmpresaId" ON public."ContactosAgenda" USING btree ("EmpresaId");

CREATE INDEX "IX_ContactosAgenda_SubcontrataId" ON public."ContactosAgenda" USING btree ("SubcontrataId");

CREATE INDEX "IX_ContactosAgenda_TenantId_CentroId" ON public."ContactosAgenda" USING btree ("TenantId", "CentroId");

CREATE INDEX "IX_ContactosAgenda_TenantId_ClienteId" ON public."ContactosAgenda" USING btree ("TenantId", "ClienteId");

CREATE INDEX "IX_ContactosAgenda_TenantId_EmpresaId" ON public."ContactosAgenda" USING btree ("TenantId", "EmpresaId");

CREATE INDEX "IX_ContactosAgenda_TenantId_SubcontrataId" ON public."ContactosAgenda" USING btree ("TenantId", "SubcontrataId");

CREATE UNIQUE INDEX "IX_ContactosWhatsApp_TenantId_Telefono" ON public."ContactosWhatsApp" USING btree ("TenantId", "Telefono");

CREATE INDEX "IX_Conversaciones_Asunto_Trgm" ON public."Conversaciones" USING gin (upper(("Asunto")::text) public.gin_trgm_ops);

CREATE INDEX "IX_Conversaciones_FechaUltimoMensajeUtc" ON public."Conversaciones" USING btree ("FechaUltimoMensajeUtc");

CREATE INDEX "IX_Conversaciones_TenantId_Canal_Estado" ON public."Conversaciones" USING btree ("TenantId", "Canal", "Estado");

CREATE INDEX "IX_Conversaciones_TenantId_ClienteId" ON public."Conversaciones" USING btree ("TenantId", "ClienteId");

CREATE UNIQUE INDEX "IX_Conversaciones_TenantId_ConexionIntegracionId_HiloExternoId" ON public."Conversaciones" USING btree ("TenantId", "ConexionIntegracionId", "HiloExternoId");

CREATE INDEX "IX_Conversaciones_TenantId_ConexionIntegracionId_TelefonoConta~" ON public."Conversaciones" USING btree ("TenantId", "ConexionIntegracionId", "TelefonoContacto");

CREATE INDEX "IX_Conversaciones_TenantId_EmpresaId" ON public."Conversaciones" USING btree ("TenantId", "EmpresaId");

CREATE INDEX "IX_Conversaciones_TenantId_Estado" ON public."Conversaciones" USING btree ("TenantId", "Estado");

CREATE UNIQUE INDEX "IX_CredencialesAccesoEmpresa_TenantId_EmpresaId" ON public."CredencialesAccesoEmpresa" USING btree ("TenantId", "EmpresaId");

CREATE UNIQUE INDEX "IX_CredencialesAccesoSubcontrata_TenantId_SubcontrataId" ON public."CredencialesAccesoSubcontrata" USING btree ("TenantId", "SubcontrataId");

CREATE UNIQUE INDEX "IX_CredencialesIntegracion_TenantId_ConexionIntegracionId" ON public."CredencialesIntegracion" USING btree ("TenantId", "ConexionIntegracionId");

CREATE INDEX "IX_DelegacionesTenant_TenantConsultoraId_TenantClienteId_Activa" ON public."DelegacionesTenant" USING btree ("TenantConsultoraId", "TenantClienteId", "Activa");

CREATE INDEX "IX_DetallesSugerenciaGestionCorreo_SugerenciaGestionCorreoId" ON public."DetallesSugerenciaGestionCorreo" USING btree ("SugerenciaGestionCorreoId");

CREATE INDEX "IX_DeteccionesTrabajador_DocumentoId_Resuelta" ON public."DeteccionesTrabajador" USING btree ("DocumentoId", "Resuelta");

CREATE INDEX "IX_DeteccionesTrabajador_EmpresaId_Resuelta" ON public."DeteccionesTrabajador" USING btree ("EmpresaId", "Resuelta");

CREATE INDEX "IX_DocumentosGenerados_DocumentoId" ON public."DocumentosGenerados" USING btree ("DocumentoId");

CREATE INDEX "IX_DocumentosGenerados_PlantillaDocumentoVersionId" ON public."DocumentosGenerados" USING btree ("PlantillaDocumentoVersionId");

CREATE INDEX "IX_DocumentosGenerados_TenantId_EmpresaId" ON public."DocumentosGenerados" USING btree ("TenantId", "EmpresaId");

CREATE INDEX "IX_DocumentosGenerados_TenantId_PlantillaDocumentoVersionId" ON public."DocumentosGenerados" USING btree ("TenantId", "PlantillaDocumentoVersionId");

CREATE INDEX "IX_DocumentosGenerados_TenantId_TrabajadorId" ON public."DocumentosGenerados" USING btree ("TenantId", "TrabajadorId");

CREATE INDEX "IX_Documentos_ClienteId_TipoDocumentoId" ON public."Documentos" USING btree ("ClienteId", "TipoDocumentoId");

CREATE INDEX "IX_Documentos_EmpresaId_TipoDocumentoId" ON public."Documentos" USING btree ("EmpresaId", "TipoDocumentoId");

CREATE INDEX "IX_Documentos_ProyectoId_TipoDocumentoId" ON public."Documentos" USING btree ("ProyectoId", "TipoDocumentoId");

CREATE INDEX "IX_Documentos_TenantId_ClienteId" ON public."Documentos" USING btree ("TenantId", "ClienteId");

CREATE INDEX "IX_Documentos_TenantId_EmpresaId" ON public."Documentos" USING btree ("TenantId", "EmpresaId");

CREATE INDEX "IX_Documentos_TenantId_FechaVencimiento" ON public."Documentos" USING btree ("TenantId", "FechaVencimiento") WHERE (NOT "EstaEliminado");

CREATE UNIQUE INDEX "IX_Documentos_TenantId_Id" ON public."Documentos" USING btree ("TenantId", "Id");

CREATE INDEX "IX_Documentos_TenantId_ProyectoId" ON public."Documentos" USING btree ("TenantId", "ProyectoId");

CREATE INDEX "IX_Documentos_TenantId_TipoDocumentoId" ON public."Documentos" USING btree ("TenantId", "TipoDocumentoId");

CREATE INDEX "IX_Documentos_TenantId_TrabajadorId" ON public."Documentos" USING btree ("TenantId", "TrabajadorId");

CREATE INDEX "IX_Documentos_TenantId_VehiculoId" ON public."Documentos" USING btree ("TenantId", "VehiculoId");

CREATE INDEX "IX_Documentos_TrabajadorId_TipoDocumentoId" ON public."Documentos" USING btree ("TrabajadorId", "TipoDocumentoId");

CREATE INDEX "IX_Documentos_VehiculoId_TipoDocumentoId" ON public."Documentos" USING btree ("VehiculoId", "TipoDocumentoId");

CREATE INDEX "IX_DominiosProveedorPlataformaCae_Dominio" ON public."DominiosProveedorPlataformaCae" USING btree ("Dominio");

CREATE INDEX "IX_Empresas_EjecutivoUsuarioId" ON public."Empresas" USING btree ("EjecutivoUsuarioId");

CREATE INDEX "IX_Empresas_RazonSocial_Trgm" ON public."Empresas" USING gin (upper(("RazonSocial")::text) public.gin_trgm_ops);

CREATE UNIQUE INDEX "IX_Empresas_TenantId_Cif" ON public."Empresas" USING btree ("TenantId", "Cif");

CREATE INDEX "IX_Empresas_TenantId_EsPropia" ON public."Empresas" USING btree ("TenantId", "EsPropia");

CREATE UNIQUE INDEX "IX_Empresas_TenantId_Id" ON public."Empresas" USING btree ("TenantId", "Id");

CREATE UNIQUE INDEX "IX_Empresas_TenantId_RazonSocial" ON public."Empresas" USING btree ("TenantId", "RazonSocial");

CREATE UNIQUE INDEX "IX_EstadosAutomatizacion_TenantId_TrabajoId" ON public."EstadosAutomatizacion" USING btree ("TenantId", "TrabajoId");

CREATE INDEX "IX_EventosConversacion_ConversacionId" ON public."EventosConversacion" USING btree ("ConversacionId");

CREATE INDEX "IX_EventosRecientesUsuario_TenantId_UsuarioId_OcurridoEnUtc" ON public."EventosRecientesUsuario" USING btree ("TenantId", "UsuarioId", "OcurridoEnUtc" DESC);

CREATE INDEX "IX_EventosWebhook_ConexionIntegracionId" ON public."EventosWebhook" USING btree ("ConexionIntegracionId");

CREATE INDEX "IX_EventosWebhook_Estado_FechaRecepcionUtc_SinRedactar" ON public."EventosWebhook" USING btree ("Estado", "FechaRecepcionUtc") WHERE ("PayloadRedactado" = false);

CREATE INDEX "IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes" ON public."EventosWebhook" USING btree ("TenantId", "FechaRecepcionUtc") WHERE ("Estado" = 'Pendiente'::text);

CREATE INDEX "IX_ExtraccionesIaCacheDocumentos_TenantId_DocumentoId" ON public."ExtraccionesIaCacheDocumentos" USING btree ("TenantId", "DocumentoId");

CREATE UNIQUE INDEX "IX_ExtraccionesIaCacheDocumentos_TenantId_ExtraccionIaCacheId_~" ON public."ExtraccionesIaCacheDocumentos" USING btree ("TenantId", "ExtraccionIaCacheId", "DocumentoId");

CREATE UNIQUE INDEX "IX_ExtraccionesIaCache_TenantId_HashSha256_TipoEsperado_Versio~" ON public."ExtraccionesIaCache" USING btree ("TenantId", "HashSha256", "TipoEsperado", "VersionPipeline");

CREATE UNIQUE INDEX "IX_ExtraccionesIaCache_TenantId_Id" ON public."ExtraccionesIaCache" USING btree ("TenantId", "Id");

CREATE INDEX "IX_FiltrosGuardados_UsuarioId_Pantalla" ON public."FiltrosGuardados" USING btree ("UsuarioId", "Pantalla");

CREATE INDEX "IX_FirmasDigitalesDocumento_DocumentoId" ON public."FirmasDigitalesDocumento" USING btree ("DocumentoId");

CREATE INDEX "IX_FirmasDigitalesDocumento_TenantId_DocumentoId" ON public."FirmasDigitalesDocumento" USING btree ("TenantId", "DocumentoId");

CREATE INDEX "IX_FirmasEnCampoDocumento_DocumentoId" ON public."FirmasEnCampoDocumento" USING btree ("DocumentoId");

CREATE INDEX "IX_FirmasEnCampoDocumento_TenantId_DocumentoId" ON public."FirmasEnCampoDocumento" USING btree ("TenantId", "DocumentoId");

CREATE UNIQUE INDEX "IX_FirmasGuardadasUsuario_TenantId_UsuarioId" ON public."FirmasGuardadasUsuario" USING btree ("TenantId", "UsuarioId");

CREATE INDEX "IX_Gestiones_CentroId" ON public."Gestiones" USING btree ("CentroId");

CREATE INDEX "IX_Gestiones_TenantId_CentroId" ON public."Gestiones" USING btree ("TenantId", "CentroId");

CREATE INDEX "IX_Gestiones_TenantId_TipoDocumentoId" ON public."Gestiones" USING btree ("TenantId", "TipoDocumentoId");

CREATE INDEX "IX_Gestiones_TenantId_TrabajadorId" ON public."Gestiones" USING btree ("TenantId", "TrabajadorId");

CREATE INDEX "IX_Gestiones_TrabajadorId" ON public."Gestiones" USING btree ("TrabajadorId");

CREATE INDEX "IX_HistorialImportaciones_TenantId_EjecutadaEnUtc" ON public."HistorialImportaciones" USING btree ("TenantId", "EjecutadaEnUtc");

CREATE INDEX "IX_HistorialInformes_TenantId_GeneradoEnUtc" ON public."HistorialInformes" USING btree ("TenantId", "GeneradoEnUtc");

CREATE INDEX "IX_IncidenciasPurga_TenantId_SolicitudPurgaId" ON public."IncidenciasPurga" USING btree ("TenantId", "SolicitudPurgaId");

CREATE INDEX "IX_Incidencias_CentroId" ON public."Incidencias" USING btree ("CentroId");

CREATE INDEX "IX_Incidencias_Descripcion_Trgm" ON public."Incidencias" USING gin (upper(("Descripcion")::text) public.gin_trgm_ops);

CREATE INDEX "IX_Incidencias_TenantId_CentroId" ON public."Incidencias" USING btree ("TenantId", "CentroId");

CREATE INDEX "IX_Incidencias_TenantId_TrabajadorId" ON public."Incidencias" USING btree ("TenantId", "TrabajadorId");

CREATE INDEX "IX_Incidencias_TrabajadorId" ON public."Incidencias" USING btree ("TrabajadorId");

CREATE INDEX "IX_InstruccionesTratamientoIaTenantPropietario_TenantId_FechaA~" ON public."InstruccionesTratamientoIaTenantPropietario" USING btree ("TenantId", "FechaAceptacionUtc");

CREATE UNIQUE INDEX "IX_InstruccionesTratamientoIaTenantPropietario_TenantId_Vigente" ON public."InstruccionesTratamientoIaTenantPropietario" USING btree ("TenantId") WHERE ("RevocadaEnUtc" IS NULL);

CREATE INDEX "IX_ItemsGeneracionDocumento_TenantId_LoteGeneracionDocumentoId" ON public."ItemsGeneracionDocumento" USING btree ("TenantId", "LoteGeneracionDocumentoId");

CREATE UNIQUE INDEX "IX_LineasWhatsApp_ConexionIntegracionId" ON public."LineasWhatsApp" USING btree ("ConexionIntegracionId");

CREATE UNIQUE INDEX "IX_LineasWhatsApp_PhoneNumberId" ON public."LineasWhatsApp" USING btree ("PhoneNumberId");

CREATE UNIQUE INDEX "IX_LineasWhatsApp_TenantId_ConexionIntegracionId" ON public."LineasWhatsApp" USING btree ("TenantId", "ConexionIntegracionId");

CREATE INDEX "IX_LotesGeneracionDocumento_PlantillaDocumentoVersionId" ON public."LotesGeneracionDocumento" USING btree ("PlantillaDocumentoVersionId");

CREATE UNIQUE INDEX "IX_LotesGeneracionDocumento_TenantId_Id" ON public."LotesGeneracionDocumento" USING btree ("TenantId", "Id");

CREATE INDEX "IX_LotesGeneracionDocumento_TenantId_PlantillaDocumentoVersion~" ON public."LotesGeneracionDocumento" USING btree ("TenantId", "PlantillaDocumentoVersionId");

CREATE INDEX "IX_MacrosRespuesta_TenantId_ClienteId" ON public."MacrosRespuesta" USING btree ("TenantId", "ClienteId");

CREATE INDEX "IX_Mensajes_ConversacionId" ON public."Mensajes" USING btree ("ConversacionId");

CREATE INDEX "IX_Mensajes_FechaUtc" ON public."Mensajes" USING btree ("FechaUtc");

CREATE UNIQUE INDEX "IX_Mensajes_TenantId_MensajeExternoId" ON public."Mensajes" USING btree ("TenantId", "MensajeExternoId");

CREATE UNIQUE INDEX "IX_MiembrosPoolLinea_LineaWhatsAppId_UsuarioId" ON public."MiembrosPoolLinea" USING btree ("LineaWhatsAppId", "UsuarioId");

CREATE INDEX "IX_NotasInternasConversacion_ConversacionId" ON public."NotasInternasConversacion" USING btree ("ConversacionId");

CREATE INDEX "IX_NotificacionesUsuario_UsuarioDestinatarioId_Leida" ON public."NotificacionesUsuario" USING btree ("UsuarioDestinatarioId", "Leida");

CREATE UNIQUE INDEX "IX_OperacionesImportacion_TenantId_OperacionId" ON public."OperacionesImportacion" USING btree ("TenantId", "OperacionId");

CREATE UNIQUE INDEX "IX_ParametrosSistema_TenantId" ON public."ParametrosSistema" USING btree ("TenantId");

CREATE INDEX "IX_ParticipantesConversacion_ConversacionId" ON public."ParticipantesConversacion" USING btree ("ConversacionId");

CREATE UNIQUE INDEX "IX_PasosTareaAsistente_TareaAsistenteId_Posicion" ON public."PasosTareaAsistente" USING btree ("TareaAsistenteId", "Posicion");

CREATE INDEX "IX_PlantillasDocumentoVersion_PlantillaDocumentoId" ON public."PlantillasDocumentoVersion" USING btree ("PlantillaDocumentoId");

CREATE UNIQUE INDEX "IX_PlantillasDocumentoVersion_TenantId_PlantillaDocumentoId_Nu~" ON public."PlantillasDocumentoVersion" USING btree ("TenantId", "PlantillaDocumentoId", "NumeroVersion");

CREATE INDEX "IX_PlantillasDocumento_TenantId_CentroId" ON public."PlantillasDocumento" USING btree ("TenantId", "CentroId");

CREATE INDEX "IX_PlantillasDocumento_TenantId_ClienteId" ON public."PlantillasDocumento" USING btree ("TenantId", "ClienteId");

CREATE UNIQUE INDEX "IX_PlantillasDocumento_TenantId_Id" ON public."PlantillasDocumento" USING btree ("TenantId", "Id");

CREATE INDEX "IX_PlantillasDocumento_TenantId_TipoDocumentoId" ON public."PlantillasDocumento" USING btree ("TenantId", "TipoDocumentoId");

CREATE INDEX "IX_PlantillasDocumento_VersionActualId" ON public."PlantillasDocumento" USING btree ("VersionActualId");

CREATE INDEX "IX_PlantillasElemento_PlantillaDocumentoVersionId" ON public."PlantillasElemento" USING btree ("PlantillaDocumentoVersionId");

CREATE UNIQUE INDEX "IX_PreferenciasDashboardUsuario_UsuarioId" ON public."PreferenciasDashboardUsuario" USING btree ("UsuarioId");

CREATE UNIQUE INDEX "IX_ProveedoresPlataformaCae_Codigo" ON public."ProveedoresPlataformaCae" USING btree ("Codigo");

CREATE UNIQUE INDEX "IX_ProyectosTecnicos_TenantId_ProyectoId_TrabajadorId_Activo" ON public."ProyectosTecnicos" USING btree ("TenantId", "ProyectoId", "TrabajadorId") WHERE ("FechaBaja" IS NULL);

CREATE INDEX "IX_ProyectosTecnicos_TenantId_TrabajadorId" ON public."ProyectosTecnicos" USING btree ("TenantId", "TrabajadorId");

CREATE INDEX "IX_ProyectosTecnicos_TrabajadorId" ON public."ProyectosTecnicos" USING btree ("TrabajadorId");

CREATE INDEX "IX_Proyectos_CentroId" ON public."Proyectos" USING btree ("CentroId");

CREATE INDEX "IX_Proyectos_TenantId_CentroId_ClienteId" ON public."Proyectos" USING btree ("TenantId", "CentroId", "ClienteId");

CREATE UNIQUE INDEX "IX_Proyectos_TenantId_ClienteId_Nombre" ON public."Proyectos" USING btree ("TenantId", "ClienteId", "Nombre") WHERE (NOT "EstaEliminado");

CREATE UNIQUE INDEX "IX_Proyectos_TenantId_Id" ON public."Proyectos" USING btree ("TenantId", "Id");

CREATE INDEX "IX_RechazosAcreditacionDocumentoPlataforma_AcreditacionId" ON public."RechazosAcreditacionDocumentoPlataforma" USING btree ("AcreditacionId");

CREATE INDEX "IX_RechazosAcreditacionDocumentoPlataforma_TenantId_Acreditaci~" ON public."RechazosAcreditacionDocumentoPlataforma" USING btree ("TenantId", "AcreditacionId");

CREATE UNIQUE INDEX "IX_ReclamacionesBuzonIntegracion_BuzonEmail" ON public."ReclamacionesBuzonIntegracion" USING btree ("BuzonEmail");

CREATE UNIQUE INDEX "IX_ReclamacionesBuzonIntegracion_ConexionIntegracionId" ON public."ReclamacionesBuzonIntegracion" USING btree ("ConexionIntegracionId");

CREATE INDEX "IX_ReclamacionesDocumentalesDocumentos_ReclamacionDocumentalId" ON public."ReclamacionesDocumentalesDocumentos" USING btree ("ReclamacionDocumentalId");

CREATE INDEX "IX_ReclamacionesDocumentalesDocumentos_TenantId_DocumentoId" ON public."ReclamacionesDocumentalesDocumentos" USING btree ("TenantId", "DocumentoId");

CREATE INDEX "IX_ReclamacionesDocumentales_ConversacionId" ON public."ReclamacionesDocumentales" USING btree ("ConversacionId");

CREATE INDEX "IX_ReclamacionesDocumentales_TenantId_ClienteId" ON public."ReclamacionesDocumentales" USING btree ("TenantId", "ClienteId");

CREATE INDEX "IX_ReclamacionesDocumentales_TenantId_EmpresaId" ON public."ReclamacionesDocumentales" USING btree ("TenantId", "EmpresaId");

CREATE INDEX "IX_RegistrosAccesoDocumentoSensible_DocumentoId_OcurridoEnUtc" ON ONLY public."RegistrosAccesoDocumentoSensible" USING btree ("DocumentoId", "OcurridoEnUtc");

CREATE INDEX "IX_RegistrosAccesoDocumentoSensible_TenantId_ActorRealUsuarioId" ON ONLY public."RegistrosAccesoDocumentoSensible" USING btree ("TenantId", "ActorRealUsuarioId");

CREATE INDEX "IX_RegistrosAccesoDocumentoSensible_TenantId_OcurridoEnUtc" ON ONLY public."RegistrosAccesoDocumentoSensible" USING btree ("TenantId", "OcurridoEnUtc");

CREATE INDEX "IX_RegistrosAccesoDocumentoSensible_TenantId_UsuarioId" ON ONLY public."RegistrosAccesoDocumentoSensible" USING btree ("TenantId", "UsuarioId");

CREATE INDEX "IX_RegistrosActividadSoporte_DelegacionTenantId_OcurridaEnUtc" ON public."RegistrosActividadSoporte" USING btree ("DelegacionTenantId", "OcurridaEnUtc");

CREATE INDEX "IX_RegistrosActividadSoporte_SesionPrivilegiadaId_OcurridaEnUtc" ON public."RegistrosActividadSoporte" USING btree ("SesionPrivilegiadaId", "OcurridaEnUtc");

CREATE INDEX "IX_RegistrosActividadSoporte_TenantId_UsuarioSoporteId" ON public."RegistrosActividadSoporte" USING btree ("TenantId", "UsuarioSoporteId");

CREATE INDEX "IX_RegistrosAuditoria_TenantId_ActorRealUsuarioId_FechaUtc" ON ONLY public."RegistrosAuditoria" USING btree ("TenantId", "ActorRealUsuarioId", "FechaUtc") WHERE ("ActorRealUsuarioId" IS NOT NULL);

CREATE INDEX "IX_RegistrosAuditoria_TenantId_EntidadTipo_EntidadId_FechaUtc" ON ONLY public."RegistrosAuditoria" USING btree ("TenantId", "EntidadTipo", "EntidadId", "FechaUtc");

CREATE INDEX "IX_RegistrosAuditoria_TenantId_FechaUtc" ON ONLY public."RegistrosAuditoria" USING btree ("TenantId", "FechaUtc");

CREATE INDEX "IX_RegistrosAuditoria_TenantId_UsuarioId_FechaUtc" ON ONLY public."RegistrosAuditoria" USING btree ("TenantId", "UsuarioId", "FechaUtc");

CREATE INDEX "IX_RegistrosAuditoria_TenantId_ViaAccesoId" ON ONLY public."RegistrosAuditoria" USING btree ("TenantId", "ViaAccesoId") WHERE ("ViaAccesoId" IS NOT NULL);

CREATE INDEX "IX_RegistrosTiempoGestion_ConversacionId" ON public."RegistrosTiempoGestion" USING btree ("ConversacionId");

CREATE INDEX "IX_RegistrosTiempoGestion_TenantId_ClienteId_FinUtc" ON public."RegistrosTiempoGestion" USING btree ("TenantId", "ClienteId", "FinUtc");

CREATE INDEX "IX_RegistrosTiempoGestion_TenantId_UsuarioId_FinUtc" ON public."RegistrosTiempoGestion" USING btree ("TenantId", "UsuarioId", "FinUtc");

CREATE UNIQUE INDEX "IX_RelacionesEmpresariales_ParActivo" ON public."RelacionesEmpresariales" USING btree ("TenantId", "ProveedoraId", "ClienteId") WHERE ("VigenciaHasta" IS NULL);

CREATE INDEX "IX_RelacionesEmpresariales_TenantId_ClienteId" ON public."RelacionesEmpresariales" USING btree ("TenantId", "ClienteId") INCLUDE ("ProveedoraId");

CREATE INDEX "IX_RelacionesEmpresariales_TenantId_EnmarcadaEnId" ON public."RelacionesEmpresariales" USING btree ("TenantId", "EnmarcadaEnId");

CREATE UNIQUE INDEX "IX_RelacionesEmpresariales_TenantId_Id" ON public."RelacionesEmpresariales" USING btree ("TenantId", "Id");

CREATE INDEX "IX_RelacionesEmpresariales_TenantId_ProveedoraId" ON public."RelacionesEmpresariales" USING btree ("TenantId", "ProveedoraId") INCLUDE ("ClienteId");

CREATE UNIQUE INDEX "IX_RevisionesIaDocumento_AuditoriaExtraccionIaId" ON public."RevisionesIaDocumento" USING btree ("AuditoriaExtraccionIaId");

CREATE INDEX "IX_RevisionesIaDocumento_DocumentoId_Resuelta" ON public."RevisionesIaDocumento" USING btree ("DocumentoId", "Resuelta");

CREATE INDEX "IX_SellosEmpresa_EmpresaId" ON public."SellosEmpresa" USING btree ("EmpresaId");

CREATE UNIQUE INDEX "IX_SellosEmpresa_TenantId_EmpresaId" ON public."SellosEmpresa" USING btree ("TenantId", "EmpresaId");

CREATE INDEX "IX_SesionesPrivilegiadas_Abiertas" ON public."SesionesPrivilegiadas" USING btree ("ExpiraEnUtc") WHERE ("CerradaEnUtc" IS NULL);

CREATE INDEX "IX_SesionesPrivilegiadas_ConcesionPrivilegioId" ON public."SesionesPrivilegiadas" USING btree ("ConcesionPrivilegioId");

CREATE INDEX "IX_SesionesPrivilegiadas_TenantObjetivoId_InicioEnUtc" ON public."SesionesPrivilegiadas" USING btree ("TenantObjetivoId", "InicioEnUtc");

CREATE INDEX "IX_SolicitudesCertificacionTgss_TenantId_ClienteId" ON public."SolicitudesCertificacionTgss" USING btree ("TenantId", "ClienteId");

CREATE INDEX "IX_SolicitudesCertificacionTgss_TenantId_EmpresaId_ClienteId_F~" ON public."SolicitudesCertificacionTgss" USING btree ("TenantId", "EmpresaId", "ClienteId", "FechaSolicitud");

CREATE INDEX "IX_SolicitudesConexionMicrosoft365_FechaExpiracionUtc" ON public."SolicitudesConexionMicrosoft365" USING btree ("FechaExpiracionUtc");

CREATE INDEX "IX_SolicitudesIncorporacionCartera_AsignacionCarteraId" ON public."SolicitudesIncorporacionCartera" USING btree ("AsignacionCarteraId");

CREATE INDEX "IX_SolicitudesIncorporacionCartera_AsignacionOperacionId_Propi~" ON public."SolicitudesIncorporacionCartera" USING btree ("AsignacionOperacionId", "PropietarioTenantId");

CREATE INDEX "IX_SolicitudesIncorporacionCartera_OperadorTenantId_Estado" ON public."SolicitudesIncorporacionCartera" USING btree ("OperadorTenantId", "Estado");

CREATE UNIQUE INDEX "IX_SolicitudesIncorporacionCartera_PendienteUnica" ON public."SolicitudesIncorporacionCartera" USING btree ("AsignacionOperacionId", "SolicitanteUsuarioId") WHERE (("Estado")::text = 'Pendiente'::text);

CREATE INDEX "IX_SolicitudesIncorporacionCartera_SolicitanteUsuarioId" ON public."SolicitudesIncorporacionCartera" USING btree ("SolicitanteUsuarioId");

CREATE INDEX "IX_SolicitudesPrioridadDocumento_CentroId" ON public."SolicitudesPrioridadDocumento" USING btree ("CentroId");

CREATE INDEX "IX_SolicitudesPurga_TipoDato_Estado" ON public."SolicitudesPurga" USING btree ("TipoDato", "Estado");

CREATE INDEX "IX_SugerenciasGestionCorreo_MensajeId" ON public."SugerenciasGestionCorreo" USING btree ("MensajeId");

CREATE INDEX "IX_SugerenciasVisitaCorreo_MensajeId" ON public."SugerenciasVisitaCorreo" USING btree ("MensajeId");

CREATE INDEX "IX_SuscripcionesWebhook_FechaExpiracionUtc" ON public."SuscripcionesWebhook" USING btree ("FechaExpiracionUtc");

CREATE UNIQUE INDEX "IX_SuscripcionesWebhook_TenantId_ConexionIntegracionId" ON public."SuscripcionesWebhook" USING btree ("TenantId", "ConexionIntegracionId");

CREATE INDEX "IX_TareasAsistente_TenantId_ActorRealUsuarioId_ActualizadaEnUtc" ON public."TareasAsistente" USING btree ("TenantId", "ActorRealUsuarioId", "ActualizadaEnUtc");

CREATE INDEX "IX_TarifasCliente_ClienteId" ON public."TarifasCliente" USING btree ("ClienteId");

CREATE UNIQUE INDEX "IX_TarifasCliente_TenantId_ClienteId_Concepto" ON public."TarifasCliente" USING btree ("TenantId", "ClienteId", "Concepto") WHERE (NOT "EstaEliminado");

CREATE UNIQUE INDEX "IX_TenantsAlcanzadosPorConcesion_ConcesionPrivilegioId_TenantId" ON public."TenantsAlcanzadosPorConcesion" USING btree ("ConcesionPrivilegioId", "TenantId");

CREATE INDEX "IX_TenantsAlcanzadosPorConcesion_TenantId" ON public."TenantsAlcanzadosPorConcesion" USING btree ("TenantId");

CREATE INDEX "IX_Tenants_StripeSubscriptionId" ON public."Tenants" USING btree ("StripeSubscriptionId") WHERE ("StripeSubscriptionId" IS NOT NULL);

CREATE UNIQUE INDEX "IX_TiposDocumentoAlias_TenantId_TipoDocumentoId_Texto" ON public."TiposDocumentoAlias" USING btree ("TenantId", "TipoDocumentoId", "Texto");

CREATE INDEX "IX_TiposDocumentoAlias_TipoDocumentoId" ON public."TiposDocumentoAlias" USING btree ("TipoDocumentoId");

CREATE INDEX "IX_TiposDocumentoCentros_CentroId" ON public."TiposDocumentoCentros" USING btree ("CentroId");

CREATE INDEX "IX_TiposDocumentoCentros_TenantId_CentroId" ON public."TiposDocumentoCentros" USING btree ("TenantId", "CentroId");

CREATE UNIQUE INDEX "IX_TiposDocumentoCentros_TenantId_TipoDocumentoId_CentroId" ON public."TiposDocumentoCentros" USING btree ("TenantId", "TipoDocumentoId", "CentroId");

CREATE UNIQUE INDEX "IX_TiposDocumento_TenantId_Id" ON public."TiposDocumento" USING btree ("TenantId", "Id");

CREATE UNIQUE INDEX "IX_TiposDocumento_TenantId_Nombre" ON public."TiposDocumento" USING btree ("TenantId", "Nombre");

CREATE INDEX "IX_Trabajadores_Alias_Trgm" ON public."Trabajadores" USING gin (upper(("Alias")::text) public.gin_trgm_ops);

CREATE INDEX "IX_Trabajadores_Apellidos_Trgm" ON public."Trabajadores" USING gin (upper(("Apellidos")::text) public.gin_trgm_ops);

CREATE INDEX "IX_Trabajadores_Dni_Trgm" ON public."Trabajadores" USING gin (upper(("Dni")::text) public.gin_trgm_ops);

CREATE INDEX "IX_Trabajadores_EmpresaId" ON public."Trabajadores" USING btree ("EmpresaId");

CREATE INDEX "IX_Trabajadores_Nombre_Trgm" ON public."Trabajadores" USING gin (upper(("Nombre")::text) public.gin_trgm_ops);

CREATE INDEX "IX_Trabajadores_SubcontrataId" ON public."Trabajadores" USING btree ("SubcontrataId");

CREATE UNIQUE INDEX "IX_Trabajadores_TenantId_Dni" ON public."Trabajadores" USING btree ("TenantId", "Dni") WHERE ("Dni" IS NOT NULL);

CREATE INDEX "IX_Trabajadores_TenantId_EmpresaId" ON public."Trabajadores" USING btree ("TenantId", "EmpresaId");

CREATE UNIQUE INDEX "IX_Trabajadores_TenantId_Id" ON public."Trabajadores" USING btree ("TenantId", "Id");

CREATE INDEX "IX_Trabajadores_TenantId_SubcontrataId" ON public."Trabajadores" USING btree ("TenantId", "SubcontrataId");

CREATE INDEX "IX_Trabajadores_TenantId_Telefono" ON public."Trabajadores" USING btree ("TenantId", "Telefono");

CREATE INDEX "IX_TrabajosAnalisisDocumento_TenantId_Estado_CreadoEnUtc" ON public."TrabajosAnalisisDocumento" USING btree ("TenantId", "Estado", "CreadoEnUtc");

CREATE UNIQUE INDEX "IX_TurnosTareaAsistente_TareaAsistenteId_Numero" ON public."TurnosTareaAsistente" USING btree ("TareaAsistenteId", "Numero");

CREATE UNIQUE INDEX "IX_UltimosResumenesNotificacionPlataforma_TenantId_ClienteId_P~" ON public."UltimosResumenesNotificacionPlataforma" USING btree ("TenantId", "ClienteId", "ProveedorPlataformaCaeId");

CREATE INDEX "IX_Vehiculos_EmpresaId" ON public."Vehiculos" USING btree ("EmpresaId");

CREATE INDEX "IX_Vehiculos_Modelo_Trgm" ON public."Vehiculos" USING gin (upper(("Modelo")::text) public.gin_trgm_ops);

CREATE INDEX "IX_Vehiculos_Nombre_Trgm" ON public."Vehiculos" USING gin (upper(("Nombre")::text) public.gin_trgm_ops);

CREATE INDEX "IX_Vehiculos_NumeroPlaca_Trgm" ON public."Vehiculos" USING gin (upper(("NumeroPlaca")::text) public.gin_trgm_ops);

CREATE INDEX "IX_Vehiculos_SubcontrataId" ON public."Vehiculos" USING btree ("SubcontrataId");

CREATE INDEX "IX_Vehiculos_TenantId_EmpresaId" ON public."Vehiculos" USING btree ("TenantId", "EmpresaId");

CREATE UNIQUE INDEX "IX_Vehiculos_TenantId_NumeroPlaca" ON public."Vehiculos" USING btree ("TenantId", "NumeroPlaca");

CREATE INDEX "IX_Vehiculos_TenantId_SubcontrataId" ON public."Vehiculos" USING btree ("TenantId", "SubcontrataId");

CREATE INDEX "IX_VerificacionesDocumentoOficial_DocumentoId" ON public."VerificacionesDocumentoOficial" USING btree ("DocumentoId");

CREATE UNIQUE INDEX "IX_VerificacionesDocumentoOficial_TenantId_DocumentoId" ON public."VerificacionesDocumentoOficial" USING btree ("TenantId", "DocumentoId");

CREATE INDEX "IX_VerificacionesExternaSubcontrata_TenantId_CentroId" ON public."VerificacionesExternaSubcontrata" USING btree ("TenantId", "CentroId");

CREATE INDEX "IX_VerificacionesExternaSubcontrata_TenantId_SubcontrataId_Cen~" ON public."VerificacionesExternaSubcontrata" USING btree ("TenantId", "SubcontrataId", "CentroId", "TipoDocumentoId");

CREATE INDEX "IX_VerificacionesExternaSubcontrata_TenantId_TipoDocumentoId" ON public."VerificacionesExternaSubcontrata" USING btree ("TenantId", "TipoDocumentoId");

CREATE INDEX "IX_VisitasTrabajadores_TenantId_TrabajadorId" ON public."VisitasTrabajadores" USING btree ("TenantId", "TrabajadorId");

CREATE UNIQUE INDEX "IX_VisitasTrabajadores_TenantId_VisitaId_TrabajadorId" ON public."VisitasTrabajadores" USING btree ("TenantId", "VisitaId", "TrabajadorId");

CREATE INDEX "IX_VisitasTrabajadores_TrabajadorId" ON public."VisitasTrabajadores" USING btree ("TrabajadorId");

CREATE INDEX "IX_VisitasTrabajadores_VisitaId" ON public."VisitasTrabajadores" USING btree ("VisitaId");

CREATE INDEX "IX_Visitas_CentroId" ON public."Visitas" USING btree ("CentroId");

CREATE INDEX "IX_Visitas_ExpedientePendiente" ON public."Visitas" USING btree ("TenantId", "FechaFin") WHERE (("FechaHoraSolicitudUtc" IS NOT NULL) AND ("FechaHoraExpedienteCompletoUtc" IS NULL) AND (NOT "EstaEliminado") AND (NOT "EstaCancelada"));

CREATE INDEX "IX_Visitas_FechaFin" ON public."Visitas" USING btree ("FechaFin");

CREATE INDEX "IX_Visitas_TenantId_CentroId" ON public."Visitas" USING btree ("TenantId", "CentroId");

CREATE INDEX "IX_Visitas_TenantId_ConversacionOrigenId" ON public."Visitas" USING btree ("TenantId", "ConversacionOrigenId");

CREATE UNIQUE INDEX "IX_Visitas_TenantId_Id" ON public."Visitas" USING btree ("TenantId", "Id");

CREATE UNIQUE INDEX "RoleNameIndex" ON public."AspNetRoles" USING btree ("NormalizedName");

CREATE UNIQUE INDEX "UserNameIndex" ON public."AspNetUsers" USING btree ("NormalizedUserName");

CREATE TRIGGER "TR_AspNetUserRoles_SerializaAltaDeAdministrador" AFTER INSERT OR UPDATE OF "RoleId", "UserId" ON public."AspNetUserRoles" FOR EACH ROW EXECUTE FUNCTION public.app_serializar_alta_de_administrador();

CREATE TRIGGER "TR_AspNetUsers_SerializaAltaDeAdministrador" AFTER UPDATE OF "LockoutEnd", "TenantId" ON public."AspNetUsers" FOR EACH ROW WHEN (((old."LockoutEnd" IS DISTINCT FROM new."LockoutEnd") OR (old."TenantId" IS DISTINCT FROM new."TenantId"))) EXECUTE FUNCTION public.app_serializar_alta_de_administrador();

CREATE CONSTRAINT TRIGGER "TR_PasosTareaAsistente_ExigePlanConfirmado" AFTER INSERT OR UPDATE ON public."PasosTareaAsistente" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.paso_tarea_asistente_exige_plan_confirmado();

CREATE TRIGGER "TR_TareasAsistente_ConfirmacionInmutable" BEFORE UPDATE ON public."TareasAsistente" FOR EACH ROW EXECUTE FUNCTION public.tarea_asistente_confirmacion_inmutable();

ALTER TABLE ONLY public."AcreditacionesDocumentoPlataforma"
    ADD CONSTRAINT "FK_AcreditacionesDocumentoPlataforma_CanalesGestionDocumental_~" FOREIGN KEY ("TenantId", "CanalGestionDocumentalId") REFERENCES public."CanalesGestionDocumental"("TenantId", "Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."AcreditacionesDocumentoPlataforma"
    ADD CONSTRAINT "FK_AcreditacionesDocumentoPlataforma_Documentos_TenantId_Docum~" FOREIGN KEY ("TenantId", "DocumentoId") REFERENCES public."Documentos"("TenantId", "Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."AdjuntosMensaje"
    ADD CONSTRAINT "FK_AdjuntosMensaje_Mensajes_MensajeId" FOREIGN KEY ("MensajeId") REFERENCES public."Mensajes"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."AdjuntosMensaje"
    ADD CONSTRAINT "FK_AdjuntosMensaje_Mensajes_MensajeId_TenantId" FOREIGN KEY ("MensajeId", "TenantId") REFERENCES public."Mensajes"("Id", "TenantId") ON DELETE CASCADE;

ALTER TABLE ONLY public."AsignacionesCartera"
    ADD CONSTRAINT "FK_AsignacionesCartera_AsignacionesOperacion_AsignacionOperaci~" FOREIGN KEY ("AsignacionOperacionId", "PropietarioTenantId") REFERENCES public."AsignacionesOperacion"("Id", "PropietarioTenantId") ON DELETE RESTRICT;

ALTER TABLE ONLY public."AsignacionesCartera"
    ADD CONSTRAINT "FK_AsignacionesCartera_Centros_PropietarioTenantId_AmbitoCentr~" FOREIGN KEY ("PropietarioTenantId", "AmbitoCentroId") REFERENCES public."Centros"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."AsignacionesCartera"
    ADD CONSTRAINT "FK_AsignacionesCartera_Empresas_PropietarioTenantId_AmbitoRela~" FOREIGN KEY ("PropietarioTenantId", "AmbitoRelacionClienteId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."AsignacionesCartera"
    ADD CONSTRAINT "FK_AsignacionesCartera_Proyectos_PropietarioTenantId_AmbitoPro~" FOREIGN KEY ("PropietarioTenantId", "AmbitoProyectoId") REFERENCES public."Proyectos"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."AsignacionesCartera"
    ADD CONSTRAINT "FK_AsignacionesCartera_Trabajadores_PropietarioTenantId_Ambito~" FOREIGN KEY ("PropietarioTenantId", "AmbitoTrabajadorId") REFERENCES public."Trabajadores"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."AsignacionesOperacion"
    ADD CONSTRAINT "FK_AsignacionesOperacion_Centros_PropietarioTenantId_AmbitoCen~" FOREIGN KEY ("PropietarioTenantId", "AmbitoCentroId") REFERENCES public."Centros"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."AsignacionesOperacion"
    ADD CONSTRAINT "FK_AsignacionesOperacion_Empresas_PropietarioTenantId_AmbitoRe~" FOREIGN KEY ("PropietarioTenantId", "AmbitoRelacionClienteId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."AsignacionesOperacion"
    ADD CONSTRAINT "FK_AsignacionesOperacion_Proyectos_PropietarioTenantId_AmbitoP~" FOREIGN KEY ("PropietarioTenantId", "AmbitoProyectoId") REFERENCES public."Proyectos"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."AsignacionesOperacion"
    ADD CONSTRAINT "FK_AsignacionesOperacion_Trabajadores_PropietarioTenantId_Ambi~" FOREIGN KEY ("PropietarioTenantId", "AmbitoTrabajadorId") REFERENCES public."Trabajadores"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Asignaciones"
    ADD CONSTRAINT "FK_Asignaciones_Centros_TenantId_CentroId" FOREIGN KEY ("TenantId", "CentroId") REFERENCES public."Centros"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Asignaciones"
    ADD CONSTRAINT "FK_Asignaciones_Trabajadores_TenantId_TrabajadorId" FOREIGN KEY ("TenantId", "TrabajadorId") REFERENCES public."Trabajadores"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."AspNetRoleClaims"
    ADD CONSTRAINT "FK_AspNetRoleClaims_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES public."AspNetRoles"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."AspNetUserClaims"
    ADD CONSTRAINT "FK_AspNetUserClaims_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES public."AspNetUsers"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."AspNetUserLogins"
    ADD CONSTRAINT "FK_AspNetUserLogins_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES public."AspNetUsers"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."AspNetUserRoles"
    ADD CONSTRAINT "FK_AspNetUserRoles_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES public."AspNetRoles"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."AspNetUserRoles"
    ADD CONSTRAINT "FK_AspNetUserRoles_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES public."AspNetUsers"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."AspNetUserTokens"
    ADD CONSTRAINT "FK_AspNetUserTokens_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES public."AspNetUsers"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."CanalesGestionDocumental"
    ADD CONSTRAINT "FK_CanalesGestionDocumental_Centros_TenantId_CentroId" FOREIGN KEY ("TenantId", "CentroId") REFERENCES public."Centros"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."CanalesGestionDocumental"
    ADD CONSTRAINT "FK_CanalesGestionDocumental_ProveedoresPlataformaCae_Proveedor~" FOREIGN KEY ("ProveedorPlataformaCaeId") REFERENCES public."ProveedoresPlataformaCae"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Centros"
    ADD CONSTRAINT "FK_Centros_Empresas_TenantId_ClienteId" FOREIGN KEY ("TenantId", "ClienteId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Centros"
    ADD CONSTRAINT "FK_Centros_Empresas_TenantId_EmpresaId" FOREIGN KEY ("TenantId", "EmpresaId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."ContactosAgendaRoles"
    ADD CONSTRAINT "FK_ContactosAgendaRoles_ContactosAgenda_ContactoAgendaId" FOREIGN KEY ("ContactoAgendaId") REFERENCES public."ContactosAgenda"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."ContactosAgendaTiposDocumento"
    ADD CONSTRAINT "FK_ContactosAgendaTiposDocumento_ContactosAgenda_ContactoAgend~" FOREIGN KEY ("ContactoAgendaId") REFERENCES public."ContactosAgenda"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."ContactosAgendaTiposDocumento"
    ADD CONSTRAINT "FK_ContactosAgendaTiposDocumento_TiposDocumento_TipoDocumentoId" FOREIGN KEY ("TipoDocumentoId") REFERENCES public."TiposDocumento"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."ContactosAgenda"
    ADD CONSTRAINT "FK_ContactosAgenda_Centros_CentroId" FOREIGN KEY ("CentroId") REFERENCES public."Centros"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."ContactosAgenda"
    ADD CONSTRAINT "FK_ContactosAgenda_Empresas_ClienteId" FOREIGN KEY ("ClienteId") REFERENCES public."Empresas"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."ContactosAgenda"
    ADD CONSTRAINT "FK_ContactosAgenda_Empresas_EmpresaId" FOREIGN KEY ("EmpresaId") REFERENCES public."Empresas"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."ContactosAgenda"
    ADD CONSTRAINT "FK_ContactosAgenda_Empresas_SubcontrataId" FOREIGN KEY ("SubcontrataId") REFERENCES public."Empresas"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Conversaciones"
    ADD CONSTRAINT "FK_Conversaciones_Empresas_TenantId_EmpresaId" FOREIGN KEY ("TenantId", "EmpresaId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."DetallesSugerenciaGestionCorreo"
    ADD CONSTRAINT "FK_DetallesSugerenciaGestionCorreo_SugerenciasGestionCorreo_Su~" FOREIGN KEY ("SugerenciaGestionCorreoId") REFERENCES public."SugerenciasGestionCorreo"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."DocumentosGenerados"
    ADD CONSTRAINT "FK_DocumentosGenerados_Documentos_DocumentoId" FOREIGN KEY ("DocumentoId") REFERENCES public."Documentos"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."DocumentosGenerados"
    ADD CONSTRAINT "FK_DocumentosGenerados_PlantillasDocumentoVersion_PlantillaDoc~" FOREIGN KEY ("PlantillaDocumentoVersionId") REFERENCES public."PlantillasDocumentoVersion"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Documentos"
    ADD CONSTRAINT "FK_Documentos_Empresas_TenantId_ClienteId" FOREIGN KEY ("TenantId", "ClienteId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Documentos"
    ADD CONSTRAINT "FK_Documentos_Empresas_TenantId_EmpresaId" FOREIGN KEY ("TenantId", "EmpresaId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Documentos"
    ADD CONSTRAINT "FK_Documentos_Proyectos_TenantId_ProyectoId" FOREIGN KEY ("TenantId", "ProyectoId") REFERENCES public."Proyectos"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Documentos"
    ADD CONSTRAINT "FK_Documentos_TiposDocumento_TenantId_TipoDocumentoId" FOREIGN KEY ("TenantId", "TipoDocumentoId") REFERENCES public."TiposDocumento"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Documentos"
    ADD CONSTRAINT "FK_Documentos_Trabajadores_TenantId_TrabajadorId" FOREIGN KEY ("TenantId", "TrabajadorId") REFERENCES public."Trabajadores"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Documentos"
    ADD CONSTRAINT "FK_Documentos_Vehiculos_TenantId_VehiculoId" FOREIGN KEY ("TenantId", "VehiculoId") REFERENCES public."Vehiculos"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."ExtraccionesIaCacheDocumentos"
    ADD CONSTRAINT "FK_ExtraccionesIaCacheDocumentos_Documentos_TenantId_Documento~" FOREIGN KEY ("TenantId", "DocumentoId") REFERENCES public."Documentos"("TenantId", "Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."ExtraccionesIaCacheDocumentos"
    ADD CONSTRAINT "FK_ExtraccionesIaCacheDocumentos_ExtraccionesIaCache_TenantId_~" FOREIGN KEY ("TenantId", "ExtraccionIaCacheId") REFERENCES public."ExtraccionesIaCache"("TenantId", "Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."FirmasDigitalesDocumento"
    ADD CONSTRAINT "FK_FirmasDigitalesDocumento_Documentos_DocumentoId" FOREIGN KEY ("DocumentoId") REFERENCES public."Documentos"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."FirmasEnCampoDocumento"
    ADD CONSTRAINT "FK_FirmasEnCampoDocumento_Documentos_DocumentoId" FOREIGN KEY ("DocumentoId") REFERENCES public."Documentos"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."Gestiones"
    ADD CONSTRAINT "FK_Gestiones_Centros_TenantId_CentroId" FOREIGN KEY ("TenantId", "CentroId") REFERENCES public."Centros"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Gestiones"
    ADD CONSTRAINT "FK_Gestiones_TiposDocumento_TenantId_TipoDocumentoId" FOREIGN KEY ("TenantId", "TipoDocumentoId") REFERENCES public."TiposDocumento"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Gestiones"
    ADD CONSTRAINT "FK_Gestiones_Trabajadores_TenantId_TrabajadorId" FOREIGN KEY ("TenantId", "TrabajadorId") REFERENCES public."Trabajadores"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."IncidenciasPurga"
    ADD CONSTRAINT "FK_IncidenciasPurga_SolicitudesPurga_TenantId_SolicitudPurgaId" FOREIGN KEY ("TenantId", "SolicitudPurgaId") REFERENCES public."SolicitudesPurga"("TenantId", "Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."Incidencias"
    ADD CONSTRAINT "FK_Incidencias_Centros_TenantId_CentroId" FOREIGN KEY ("TenantId", "CentroId") REFERENCES public."Centros"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Incidencias"
    ADD CONSTRAINT "FK_Incidencias_Trabajadores_TenantId_TrabajadorId" FOREIGN KEY ("TenantId", "TrabajadorId") REFERENCES public."Trabajadores"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."ItemsGeneracionDocumento"
    ADD CONSTRAINT "FK_ItemsGeneracionDocumento_LotesGeneracionDocumento_TenantId_~" FOREIGN KEY ("TenantId", "LoteGeneracionDocumentoId") REFERENCES public."LotesGeneracionDocumento"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."LineasWhatsApp"
    ADD CONSTRAINT "FK_LineasWhatsApp_ConexionesIntegracion_TenantId_ConexionInteg~" FOREIGN KEY ("TenantId", "ConexionIntegracionId") REFERENCES public."ConexionesIntegracion"("TenantId", "Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."LotesGeneracionDocumento"
    ADD CONSTRAINT "FK_LotesGeneracionDocumento_PlantillasDocumentoVersion_Plantil~" FOREIGN KEY ("PlantillaDocumentoVersionId") REFERENCES public."PlantillasDocumentoVersion"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Mensajes"
    ADD CONSTRAINT "FK_Mensajes_Conversaciones_ConversacionId" FOREIGN KEY ("ConversacionId") REFERENCES public."Conversaciones"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."Mensajes"
    ADD CONSTRAINT "FK_Mensajes_Conversaciones_ConversacionId_TenantId" FOREIGN KEY ("ConversacionId", "TenantId") REFERENCES public."Conversaciones"("Id", "TenantId") ON DELETE CASCADE;

ALTER TABLE ONLY public."MiembrosPoolLinea"
    ADD CONSTRAINT "FK_MiembrosPoolLinea_LineasWhatsApp_LineaWhatsAppId" FOREIGN KEY ("LineaWhatsAppId") REFERENCES public."LineasWhatsApp"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."NotasInternasConversacion"
    ADD CONSTRAINT "FK_NotasInternasConversacion_Conversaciones_ConversacionId_Tena" FOREIGN KEY ("ConversacionId", "TenantId") REFERENCES public."Conversaciones"("Id", "TenantId") ON DELETE CASCADE;

ALTER TABLE ONLY public."ParticipantesConversacion"
    ADD CONSTRAINT "FK_ParticipantesConversacion_Conversaciones_ConversacionId" FOREIGN KEY ("ConversacionId") REFERENCES public."Conversaciones"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."ParticipantesConversacion"
    ADD CONSTRAINT "FK_ParticipantesConversacion_Conversaciones_ConversacionId_Tena" FOREIGN KEY ("ConversacionId", "TenantId") REFERENCES public."Conversaciones"("Id", "TenantId") ON DELETE CASCADE;

ALTER TABLE ONLY public."PasosTareaAsistente"
    ADD CONSTRAINT "FK_PasosTareaAsistente_TareasAsistente_TareaAsistenteId" FOREIGN KEY ("TareaAsistenteId") REFERENCES public."TareasAsistente"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."PasosTareaAsistente"
    ADD CONSTRAINT "FK_PasosTareaAsistente_TareasAsistente_TareaAsistenteId_TenantI" FOREIGN KEY ("TareaAsistenteId", "TenantId") REFERENCES public."TareasAsistente"("Id", "TenantId") ON DELETE CASCADE;

ALTER TABLE ONLY public."PlantillasDocumentoVersion"
    ADD CONSTRAINT "FK_PlantillasDocumentoVersion_PlantillasDocumento_PlantillaDoc~" FOREIGN KEY ("PlantillaDocumentoId") REFERENCES public."PlantillasDocumento"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."PlantillasDocumento"
    ADD CONSTRAINT "FK_PlantillasDocumento_PlantillasDocumentoVersion_VersionActua~" FOREIGN KEY ("VersionActualId") REFERENCES public."PlantillasDocumentoVersion"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."PlantillasDocumento"
    ADD CONSTRAINT "FK_PlantillasDocumento_TiposDocumento_TenantId_TipoDocumentoId" FOREIGN KEY ("TenantId", "TipoDocumentoId") REFERENCES public."TiposDocumento"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."PlantillasElemento"
    ADD CONSTRAINT "FK_PlantillasElemento_PlantillasDocumentoVersion_PlantillaDocu~" FOREIGN KEY ("PlantillaDocumentoVersionId") REFERENCES public."PlantillasDocumentoVersion"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."ProyectosTecnicos"
    ADD CONSTRAINT "FK_ProyectosTecnicos_Proyectos_TenantId_ProyectoId" FOREIGN KEY ("TenantId", "ProyectoId") REFERENCES public."Proyectos"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."ProyectosTecnicos"
    ADD CONSTRAINT "FK_ProyectosTecnicos_Trabajadores_TenantId_TrabajadorId" FOREIGN KEY ("TenantId", "TrabajadorId") REFERENCES public."Trabajadores"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Proyectos"
    ADD CONSTRAINT "FK_Proyectos_Centros_TenantId_CentroId_ClienteId" FOREIGN KEY ("TenantId", "CentroId", "ClienteId") REFERENCES public."Centros"("TenantId", "Id", "ClienteId") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Proyectos"
    ADD CONSTRAINT "FK_Proyectos_Empresas_TenantId_ClienteId" FOREIGN KEY ("TenantId", "ClienteId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."RechazosAcreditacionDocumentoPlataforma"
    ADD CONSTRAINT "FK_RechazosAcreditacionDocumentoPlataforma_AcreditacionesDocum~" FOREIGN KEY ("AcreditacionId") REFERENCES public."AcreditacionesDocumentoPlataforma"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."ReclamacionesDocumentalesDocumentos"
    ADD CONSTRAINT "FK_ReclamacionesDocumentalesDocumentos_ReclamacionesDocumental~" FOREIGN KEY ("ReclamacionDocumentalId") REFERENCES public."ReclamacionesDocumentales"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."ReclamacionesDocumentales"
    ADD CONSTRAINT "FK_ReclamacionesDocumentales_Conversaciones_ConversacionId" FOREIGN KEY ("ConversacionId") REFERENCES public."Conversaciones"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."ReclamacionesDocumentales"
    ADD CONSTRAINT "FK_ReclamacionesDocumentales_Empresas_TenantId_EmpresaId" FOREIGN KEY ("TenantId", "EmpresaId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."RelacionesEmpresariales"
    ADD CONSTRAINT "FK_RelacionesEmpresariales_Empresas_TenantId_ClienteId" FOREIGN KEY ("TenantId", "ClienteId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."RelacionesEmpresariales"
    ADD CONSTRAINT "FK_RelacionesEmpresariales_Empresas_TenantId_ProveedoraId" FOREIGN KEY ("TenantId", "ProveedoraId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."RelacionesEmpresariales"
    ADD CONSTRAINT "FK_RelacionesEmpresariales_RelacionesEmpresariales_TenantId_En~" FOREIGN KEY ("TenantId", "EnmarcadaEnId") REFERENCES public."RelacionesEmpresariales"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."SellosEmpresa"
    ADD CONSTRAINT "FK_SellosEmpresa_Empresas_EmpresaId" FOREIGN KEY ("EmpresaId") REFERENCES public."Empresas"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."SesionesPrivilegiadas"
    ADD CONSTRAINT "FK_SesionesPrivilegiadas_Concesion_Capacidad" FOREIGN KEY ("ConcesionPrivilegioId", "Capacidad") REFERENCES public."ConcesionesPrivilegio"("Id", "Capacidad") ON DELETE RESTRICT;

ALTER TABLE ONLY public."SesionesPrivilegiadas"
    ADD CONSTRAINT "FK_SesionesPrivilegiadas_ConcesionesPrivilegio_ConcesionPrivil~" FOREIGN KEY ("ConcesionPrivilegioId") REFERENCES public."ConcesionesPrivilegio"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."SolicitudesCertificacionTgss"
    ADD CONSTRAINT "FK_SolicitudesCertificacionTgss_Empresas_TenantId_ClienteId" FOREIGN KEY ("TenantId", "ClienteId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."SolicitudesCertificacionTgss"
    ADD CONSTRAINT "FK_SolicitudesCertificacionTgss_Empresas_TenantId_EmpresaId" FOREIGN KEY ("TenantId", "EmpresaId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."SolicitudesIncorporacionCartera"
    ADD CONSTRAINT "FK_SolicitudesIncorporacionCartera_AsignacionesCartera_Asignac~" FOREIGN KEY ("AsignacionCarteraId") REFERENCES public."AsignacionesCartera"("Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."SolicitudesIncorporacionCartera"
    ADD CONSTRAINT "FK_SolicitudesIncorporacionCartera_AsignacionesOperacion_Asign~" FOREIGN KEY ("AsignacionOperacionId", "PropietarioTenantId") REFERENCES public."AsignacionesOperacion"("Id", "PropietarioTenantId") ON DELETE RESTRICT;

ALTER TABLE ONLY public."TarifasCliente"
    ADD CONSTRAINT "FK_TarifasCliente_Empresas_TenantId_ClienteId" FOREIGN KEY ("TenantId", "ClienteId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."TenantsAlcanzadosPorConcesion"
    ADD CONSTRAINT "FK_TenantsAlcanzadosPorConcesion_ConcesionesPrivilegio_Concesi~" FOREIGN KEY ("ConcesionPrivilegioId") REFERENCES public."ConcesionesPrivilegio"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."TiposDocumentoAlias"
    ADD CONSTRAINT "FK_TiposDocumentoAlias_TiposDocumento_TipoDocumentoId" FOREIGN KEY ("TipoDocumentoId") REFERENCES public."TiposDocumento"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."TiposDocumentoCentros"
    ADD CONSTRAINT "FK_TiposDocumentoCentros_Centros_TenantId_CentroId" FOREIGN KEY ("TenantId", "CentroId") REFERENCES public."Centros"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."TiposDocumentoCentros"
    ADD CONSTRAINT "FK_TiposDocumentoCentros_TiposDocumento_TenantId_TipoDocumento~" FOREIGN KEY ("TenantId", "TipoDocumentoId") REFERENCES public."TiposDocumento"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Trabajadores"
    ADD CONSTRAINT "FK_Trabajadores_Empresas_TenantId_EmpresaId" FOREIGN KEY ("TenantId", "EmpresaId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Trabajadores"
    ADD CONSTRAINT "FK_Trabajadores_Empresas_TenantId_SubcontrataId" FOREIGN KEY ("TenantId", "SubcontrataId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."TurnosTareaAsistente"
    ADD CONSTRAINT "FK_TurnosTareaAsistente_TareasAsistente_TareaAsistenteId" FOREIGN KEY ("TareaAsistenteId") REFERENCES public."TareasAsistente"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."TurnosTareaAsistente"
    ADD CONSTRAINT "FK_TurnosTareaAsistente_TareasAsistente_TareaAsistenteId_Tenant" FOREIGN KEY ("TareaAsistenteId", "TenantId") REFERENCES public."TareasAsistente"("Id", "TenantId") ON DELETE CASCADE;

ALTER TABLE ONLY public."Vehiculos"
    ADD CONSTRAINT "FK_Vehiculos_Empresas_TenantId_EmpresaId" FOREIGN KEY ("TenantId", "EmpresaId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Vehiculos"
    ADD CONSTRAINT "FK_Vehiculos_Empresas_TenantId_SubcontrataId" FOREIGN KEY ("TenantId", "SubcontrataId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."VerificacionesDocumentoOficial"
    ADD CONSTRAINT "FK_VerificacionesDocumentoOficial_Documentos_DocumentoId" FOREIGN KEY ("DocumentoId") REFERENCES public."Documentos"("Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."VerificacionesExternaSubcontrata"
    ADD CONSTRAINT "FK_VerificacionesExternaSubcontrata_Centros_TenantId_CentroId" FOREIGN KEY ("TenantId", "CentroId") REFERENCES public."Centros"("TenantId", "Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."VerificacionesExternaSubcontrata"
    ADD CONSTRAINT "FK_VerificacionesExternaSubcontrata_Empresas_TenantId_Subcontr~" FOREIGN KEY ("TenantId", "SubcontrataId") REFERENCES public."Empresas"("TenantId", "Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."VerificacionesExternaSubcontrata"
    ADD CONSTRAINT "FK_VerificacionesExternaSubcontrata_TiposDocumento_TenantId_Ti~" FOREIGN KEY ("TenantId", "TipoDocumentoId") REFERENCES public."TiposDocumento"("TenantId", "Id") ON DELETE CASCADE;

ALTER TABLE ONLY public."VisitasTrabajadores"
    ADD CONSTRAINT "FK_VisitasTrabajadores_Trabajadores_TenantId_TrabajadorId" FOREIGN KEY ("TenantId", "TrabajadorId") REFERENCES public."Trabajadores"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."VisitasTrabajadores"
    ADD CONSTRAINT "FK_VisitasTrabajadores_Visitas_TenantId_VisitaId" FOREIGN KEY ("TenantId", "VisitaId") REFERENCES public."Visitas"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE ONLY public."Visitas"
    ADD CONSTRAINT "FK_Visitas_Centros_TenantId_CentroId" FOREIGN KEY ("TenantId", "CentroId") REFERENCES public."Centros"("TenantId", "Id") ON DELETE RESTRICT;

ALTER TABLE public."AcreditacionesDocumentoPlataforma" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."AdjuntosMensaje" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."AprobacionesDocumento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Asignaciones" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."AsignacionesCartera" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."AsignacionesOperacion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."AspNetUsers" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."AuditoriasExtraccionIa" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."CanalesGestionDocumental" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Centros" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ClasificacionesRelevanciaCae" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ClasificacionesRuidoDetalleGestion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ClasificacionesRuidoMensaje" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ClavesApi" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ConcesionesPrivilegio" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ConexionesIntegracion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ConfiguracionesIaDocumentoCliente" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ContactosAgenda" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ContactosAgendaRoles" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ContactosAgendaTiposDocumento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ContactosWhatsApp" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Conversaciones" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."CredencialesAccesoEmpresa" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."CredencialesAccesoSubcontrata" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."CredencialesIntegracion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."DetallesSugerenciaGestionCorreo" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."DeteccionesTrabajador" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Documentos" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."DocumentosGenerados" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Empresas" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."EstadoBootstrapPlataforma" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."EstadosAutomatizacion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."EventosConversacion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."EventosRecientesUsuario" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."EventosWebhook" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ExtraccionesIaCache" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ExtraccionesIaCacheDocumentos" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."FirmasDigitalesDocumento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."FirmasEnCampoDocumento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."FirmasGuardadasUsuario" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Gestiones" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."HistorialImportaciones" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."HistorialInformes" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Incidencias" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."IncidenciasPurga" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."InstruccionesTratamientoIaTenantPropietario" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ItemsGeneracionDocumento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."LineasWhatsApp" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."LotesGeneracionDocumento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."MacrosRespuesta" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Mensajes" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."MiembrosPoolLinea" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."NotasInternasConversacion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."NotificacionesUsuario" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."OperacionesImportacion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."OrdenMenuLateral" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ParametrosSistema" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ParticipantesConversacion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."PasosTareaAsistente" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."PlantillasDocumento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."PlantillasDocumentoVersion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."PlantillasElemento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Proyectos" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ProyectosTecnicos" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."RechazosAcreditacionDocumentoPlataforma" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ReclamacionesDocumentales" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."ReclamacionesDocumentalesDocumentos" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."RegistrosAccesoDocumentoSensible" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."RegistrosActividadSoporte" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."RegistrosAuditoria" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."RegistrosTiempoGestion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."RelacionesEmpresariales" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."RevisionesIaDocumento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."SellosEmpresa" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."SesionesPrivilegiadas" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."SolicitudesCertificacionTgss" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."SolicitudesConexionMicrosoft365" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."SolicitudesIncorporacionCartera" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."SolicitudesPrioridadDocumento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."SolicitudesPurga" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."SugerenciasGestionCorreo" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."SugerenciasVisitaCorreo" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."SuscripcionesWebhook" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."TareasAsistente" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."TarifasCliente" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."TenantsAlcanzadosPorConcesion" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."TiposDocumento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."TiposDocumentoAlias" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."TiposDocumentoCentros" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Trabajadores" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."TrabajosAnalisisDocumento" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."TurnosTareaAsistente" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."UltimosResumenesNotificacionPlataforma" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Vehiculos" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."VerificacionesDocumentoOficial" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."VerificacionesExternaSubcontrata" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."Visitas" ENABLE ROW LEVEL SECURITY;

ALTER TABLE public."VisitasTrabajadores" ENABLE ROW LEVEL SECURITY;

CREATE POLICY administrador_del_tenant_objetivo ON public."SesionesPrivilegiadas" FOR SELECT TO cae_app_runtime USING ((("TenantObjetivoId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND ( SELECT public.app_es_administrador_del_tenant((NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid, (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AS app_es_administrador_del_tenant)));

CREATE POLICY aislamiento_tenant ON public."AcreditacionesDocumentoPlataforma" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."AdjuntosMensaje" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."AprobacionesDocumento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Asignaciones" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."AuditoriasExtraccionIa" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."CanalesGestionDocumental" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Centros" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ClasificacionesRelevanciaCae" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ClasificacionesRuidoDetalleGestion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ClasificacionesRuidoMensaje" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ClavesApi" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ConexionesIntegracion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ConfiguracionesIaDocumentoCliente" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ContactosAgenda" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ContactosAgendaRoles" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ContactosAgendaTiposDocumento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ContactosWhatsApp" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Conversaciones" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."CredencialesAccesoEmpresa" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."CredencialesAccesoSubcontrata" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."CredencialesIntegracion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."DetallesSugerenciaGestionCorreo" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."DeteccionesTrabajador" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Documentos" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."DocumentosGenerados" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Empresas" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."EstadosAutomatizacion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."EventosConversacion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."EventosRecientesUsuario" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."EventosWebhook" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ExtraccionesIaCache" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ExtraccionesIaCacheDocumentos" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."FirmasDigitalesDocumento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."FirmasEnCampoDocumento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."FirmasGuardadasUsuario" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Gestiones" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."HistorialImportaciones" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."HistorialInformes" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Incidencias" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."IncidenciasPurga" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."InstruccionesTratamientoIaTenantPropietario" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ItemsGeneracionDocumento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."LineasWhatsApp" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."LotesGeneracionDocumento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."MacrosRespuesta" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Mensajes" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."MiembrosPoolLinea" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."NotasInternasConversacion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."NotificacionesUsuario" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."OperacionesImportacion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ParametrosSistema" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ParticipantesConversacion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."PasosTareaAsistente" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."PlantillasDocumento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."PlantillasDocumentoVersion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."PlantillasElemento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Proyectos" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ProyectosTecnicos" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."RechazosAcreditacionDocumentoPlataforma" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ReclamacionesDocumentales" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."ReclamacionesDocumentalesDocumentos" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."RegistrosAccesoDocumentoSensible" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."RegistrosActividadSoporte" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."RegistrosAuditoria" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."RegistrosTiempoGestion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."RelacionesEmpresariales" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."RevisionesIaDocumento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."SellosEmpresa" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."SolicitudesCertificacionTgss" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."SolicitudesConexionMicrosoft365" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."SolicitudesPrioridadDocumento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."SolicitudesPurga" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."SugerenciasGestionCorreo" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."SugerenciasVisitaCorreo" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."SuscripcionesWebhook" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."TareasAsistente" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."TarifasCliente" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."TiposDocumento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."TiposDocumentoAlias" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."TiposDocumentoCentros" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Trabajadores" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."TrabajosAnalisisDocumento" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."TurnosTareaAsistente" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."UltimosResumenesNotificacionPlataforma" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Vehiculos" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."VerificacionesDocumentoOficial" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."VerificacionesExternaSubcontrata" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."Visitas" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY aislamiento_tenant ON public."VisitasTrabajadores" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY cuentas_alta ON public."AspNetUsers" FOR INSERT WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY cuentas_baja ON public."AspNetUsers" FOR DELETE USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY cuentas_lectura ON public."AspNetUsers" FOR SELECT USING ((("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) OR ("Id" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid) OR ("TenantId" = (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid) OR (EXISTS ( SELECT 1
   FROM (public."AsignacionesOperadorDelegado" a
     JOIN public."DelegacionesTenant" d ON ((d."Id" = a."DelegacionTenantId")))
  WHERE ((a."UsuarioId" = "AspNetUsers"."Id") AND (d."TenantClienteId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)))) OR (EXISTS ( SELECT 1
   FROM (public."AsignacionesCartera" c
     JOIN public."AsignacionesOperacion" o ON ((o."Id" = c."AsignacionOperacionId")))
  WHERE ((c."UsuarioId" = "AspNetUsers"."Id") AND (c."PropietarioTenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND ((c."Estado")::text = 'Vigente'::text) AND (c."VigenciaDesde" <= now()) AND ((c."VigenciaHasta" IS NULL) OR (now() < c."VigenciaHasta")) AND ((o."Estado")::text = 'Vigente'::text) AND (o."VigenciaDesde" <= now()) AND ((o."VigenciaHasta" IS NULL) OR (now() < o."VigenciaHasta")) AND (o."OperadorTenantId" = "AspNetUsers"."TenantId")))) OR (EXISTS ( SELECT 1
   FROM public."RegistrosAuditoria" r
  WHERE ((r."TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (r."UsuarioId" = "AspNetUsers"."Id")))) OR (EXISTS ( SELECT 1
   FROM public."RegistrosAuditoria" r
  WHERE ((r."TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (r."ActorRealUsuarioId" = "AspNetUsers"."Id")))) OR (EXISTS ( SELECT 1
   FROM public."RegistrosAccesoDocumentoSensible" s
  WHERE ((s."TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND ((s."UsuarioId" = "AspNetUsers"."Id") OR (s."ActorRealUsuarioId" = "AspNetUsers"."Id"))))) OR (EXISTS ( SELECT 1
   FROM public."RegistrosActividadSoporte" v
  WHERE ((v."TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) AND (v."UsuarioSoporteId" = "AspNetUsers"."Id"))))));

CREATE POLICY cuentas_modificacion ON public."AspNetUsers" FOR UPDATE USING ((("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) OR ("Id" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid))) WITH CHECK (((("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) OR ("Id" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid)) AND (("Id" IS DISTINCT FROM (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid) OR ("TenantId" = (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid))));

CREATE POLICY estado_bootstrap_consumo_por_la_raiz ON public."EstadoBootstrapPlataforma" FOR UPDATE USING (("UsuarioRaizId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid)) WITH CHECK (("UsuarioRaizId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid));

CREATE POLICY estado_bootstrap_designacion_al_arrancar ON public."EstadoBootstrapPlataforma" FOR INSERT WITH CHECK ((NULLIF(current_setting('app.usuario_id'::text, true), ''::text) IS NULL));

CREATE POLICY estado_bootstrap_lectura_de_la_raiz ON public."EstadoBootstrapPlataforma" FOR SELECT USING (("UsuarioRaizId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid));

CREATE POLICY operador_de_la_solicitud ON public."SolicitudesIncorporacionCartera" USING (("OperadorTenantId" = (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid)) WITH CHECK (("OperadorTenantId" = (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid));

CREATE POLICY orden_menu_alta_por_admin_plataforma_global ON public."OrdenMenuLateral" FOR INSERT WITH CHECK (public.app_es_admin_plataforma_global((NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid));

CREATE POLICY orden_menu_cambio_por_admin_plataforma_global ON public."OrdenMenuLateral" FOR UPDATE USING (public.app_es_admin_plataforma_global((NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid)) WITH CHECK (public.app_es_admin_plataforma_global((NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid));

CREATE POLICY orden_menu_lectura_de_todos ON public."OrdenMenuLateral" FOR SELECT USING (true);

CREATE POLICY posicion_en_la_asignacion ON public."AsignacionesCartera" USING ((("PropietarioTenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) OR ("OperadorTenantId" = (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid))) WITH CHECK (("PropietarioTenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY posicion_en_la_asignacion ON public."AsignacionesOperacion" USING ((("PropietarioTenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid) OR ("OperadorTenantId" = (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid))) WITH CHECK (("PropietarioTenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));

CREATE POLICY privilegio_del_usuario ON public."ConcesionesPrivilegio" USING ((("UsuarioPlataformaId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid) OR ("ConcedidaPorUsuarioId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid))) WITH CHECK ((("UsuarioPlataformaId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid) OR (("ConcedidaPorUsuarioId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid) AND ("EsAlcanceGlobal" = false) AND ("Capacidad" IN ('Aprovisionamiento', 'RestablecimientoSegundoFactor')) AND public.app_es_admin_plataforma((NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid))));

CREATE POLICY privilegio_del_usuario ON public."SesionesPrivilegiadas" USING ((EXISTS ( SELECT 1
   FROM public."ConcesionesPrivilegio" c
  WHERE ((c."Id" = "SesionesPrivilegiadas"."ConcesionPrivilegioId") AND (c."UsuarioPlataformaId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."ConcesionesPrivilegio" c
  WHERE ((c."Id" = "SesionesPrivilegiadas"."ConcesionPrivilegioId") AND (c."UsuarioPlataformaId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid)))));

CREATE POLICY privilegio_del_usuario ON public."TenantsAlcanzadosPorConcesion" USING ((EXISTS ( SELECT 1
   FROM public."ConcesionesPrivilegio" c
  WHERE ((c."Id" = "TenantsAlcanzadosPorConcesion"."ConcesionPrivilegioId") AND (c."UsuarioPlataformaId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid))))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."ConcesionesPrivilegio" c
  WHERE ((c."Id" = "TenantsAlcanzadosPorConcesion"."ConcesionPrivilegioId") AND ((c."UsuarioPlataformaId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid) OR ((c."ConcedidaPorUsuarioId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid) AND public.app_es_admin_plataforma_sobre((NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid, "TenantsAlcanzadosPorConcesion"."TenantId")))))));

CREATE POLICY solo_su_persona ON public."PasosTareaAsistente" AS RESTRICTIVE USING ((EXISTS ( SELECT 1
   FROM public."TareasAsistente" t
  WHERE (t."Id" = "PasosTareaAsistente"."TareaAsistenteId")))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."TareasAsistente" t
  WHERE (t."Id" = "PasosTareaAsistente"."TareaAsistenteId"))));

CREATE POLICY solo_su_persona ON public."TareasAsistente" AS RESTRICTIVE USING (("ActorRealUsuarioId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid)) WITH CHECK (("ActorRealUsuarioId" = (NULLIF(current_setting('app.usuario_id'::text, true), ''::text))::uuid));

CREATE POLICY solo_su_persona ON public."TurnosTareaAsistente" AS RESTRICTIVE USING ((EXISTS ( SELECT 1
   FROM public."TareasAsistente" t
  WHERE (t."Id" = "TurnosTareaAsistente"."TareaAsistenteId")))) WITH CHECK ((EXISTS ( SELECT 1
   FROM public."TareasAsistente" t
  WHERE (t."Id" = "TurnosTareaAsistente"."TareaAsistenteId"))));

GRANT USAGE ON SCHEMA public TO cae_app_runtime;
GRANT USAGE ON SCHEMA public TO cae_app_soporte;
GRANT USAGE ON SCHEMA public TO cae_app_aprovisionamiento;

REVOKE ALL ON FUNCTION public.app_bloquear_administradores_de_tenant(p_tenant uuid) FROM PUBLIC;

REVOKE ALL ON FUNCTION public.app_claves_contexto_protegidas() FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_claves_contexto_protegidas() TO cae_app_runtime;
GRANT ALL ON FUNCTION public.app_claves_contexto_protegidas() TO cae_app_soporte;
GRANT ALL ON FUNCTION public.app_claves_contexto_protegidas() TO cae_app_aprovisionamiento;

REVOKE ALL ON FUNCTION public.app_contexto_validado(OUT tenant_id uuid, OUT tenant_origen_id uuid, OUT usuario_id uuid, OUT valido boolean) FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_contexto_validado(OUT tenant_id uuid, OUT tenant_origen_id uuid, OUT usuario_id uuid, OUT valido boolean) TO cae_app_runtime;
GRANT ALL ON FUNCTION public.app_contexto_validado(OUT tenant_id uuid, OUT tenant_origen_id uuid, OUT usuario_id uuid, OUT valido boolean) TO cae_app_soporte;
GRANT ALL ON FUNCTION public.app_contexto_validado(OUT tenant_id uuid, OUT tenant_origen_id uuid, OUT usuario_id uuid, OUT valido boolean) TO cae_app_aprovisionamiento;

REVOKE ALL ON FUNCTION public.app_ctx_tenant_id() FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_ctx_tenant_id() TO cae_app_runtime;
GRANT ALL ON FUNCTION public.app_ctx_tenant_id() TO cae_app_soporte;
GRANT ALL ON FUNCTION public.app_ctx_tenant_id() TO cae_app_aprovisionamiento;

REVOKE ALL ON FUNCTION public.app_ctx_tenant_origen_id() FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_ctx_tenant_origen_id() TO cae_app_runtime;
GRANT ALL ON FUNCTION public.app_ctx_tenant_origen_id() TO cae_app_soporte;
GRANT ALL ON FUNCTION public.app_ctx_tenant_origen_id() TO cae_app_aprovisionamiento;

REVOKE ALL ON FUNCTION public.app_ctx_usuario_id() FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_ctx_usuario_id() TO cae_app_runtime;
GRANT ALL ON FUNCTION public.app_ctx_usuario_id() TO cae_app_soporte;
GRANT ALL ON FUNCTION public.app_ctx_usuario_id() TO cae_app_aprovisionamiento;

REVOKE ALL ON FUNCTION public.app_ctx_valido() FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_ctx_valido() TO cae_app_runtime;
GRANT ALL ON FUNCTION public.app_ctx_valido() TO cae_app_soporte;
GRANT ALL ON FUNCTION public.app_ctx_valido() TO cae_app_aprovisionamiento;

REVOKE ALL ON FUNCTION public.app_cuenta_por_nombre_normalizado(nombre_normalizado text) FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_cuenta_por_nombre_normalizado(nombre_normalizado text) TO cae_app_runtime;

REVOKE ALL ON FUNCTION public.app_cuentas_por_email_normalizado(email_normalizado text) FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_cuentas_por_email_normalizado(email_normalizado text) TO cae_app_runtime;

REVOKE ALL ON FUNCTION public.app_es_admin_plataforma(usuario uuid) FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_es_admin_plataforma(usuario uuid) TO cae_app_runtime;

REVOKE ALL ON FUNCTION public.app_es_admin_plataforma_global(usuario uuid) FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_es_admin_plataforma_global(usuario uuid) TO cae_app_runtime;

REVOKE ALL ON FUNCTION public.app_es_admin_plataforma_sobre(usuario uuid, tenant uuid) FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_es_admin_plataforma_sobre(usuario uuid, tenant uuid) TO cae_app_runtime;

REVOKE ALL ON FUNCTION public.app_es_administrador_del_tenant(usuario uuid, tenant uuid) FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_es_administrador_del_tenant(usuario uuid, tenant uuid) TO cae_app_runtime;

REVOKE ALL ON FUNCTION public.app_restablecer_segundo_factor_por_soporte(p_sesion uuid, p_usuario uuid) FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_restablecer_segundo_factor_por_soporte(p_sesion uuid, p_usuario uuid) TO cae_app_soporte;

REVOKE ALL ON FUNCTION public.app_serializar_alta_de_administrador() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.app_tenant_de_clave_api(hash_clave text) FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_tenant_de_clave_api(hash_clave text) TO cae_app_runtime;

REVOKE ALL ON FUNCTION public.app_tenant_de_cuenta(cuenta_id uuid) FROM PUBLIC;
GRANT ALL ON FUNCTION public.app_tenant_de_cuenta(cuenta_id uuid) TO cae_app_runtime;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AceptacionesTerminos" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AceptacionesTerminos" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AcreditacionesDocumentoPlataforma" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AcreditacionesDocumentoPlataforma" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AdjuntosMensaje" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AdjuntosMensaje" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AprobacionesDocumento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AprobacionesDocumento" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Asignaciones" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Asignaciones" TO cae_app_soporte;
GRANT SELECT,INSERT ON TABLE public."Asignaciones" TO cae_app_aprovisionamiento;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AsignacionesCartera" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AsignacionesCartera" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AsignacionesOperacion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AsignacionesOperacion" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AsignacionesOperadorDelegado" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AsignacionesOperadorDelegado" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AspNetRoleClaims" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AspNetRoleClaims" TO cae_app_soporte;

GRANT SELECT,USAGE ON SEQUENCE public."AspNetRoleClaims_Id_seq" TO cae_app_runtime;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AspNetRoles" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AspNetRoles" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AspNetUserClaims" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AspNetUserClaims" TO cae_app_soporte;

GRANT SELECT,USAGE ON SEQUENCE public."AspNetUserClaims_Id_seq" TO cae_app_runtime;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AspNetUserLogins" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AspNetUserLogins" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AspNetUserRoles" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AspNetUserRoles" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AspNetUserTokens" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AspNetUserTokens" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AspNetUsers" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AspNetUsers" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AuditoriasExtraccionIa" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AuditoriasExtraccionIa" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."AvisosRevisionNormativa" TO cae_app_runtime;
GRANT SELECT ON TABLE public."AvisosRevisionNormativa" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CanalesGestionDocumental" TO cae_app_runtime;
GRANT SELECT ON TABLE public."CanalesGestionDocumental" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Centros" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Centros" TO cae_app_soporte;
GRANT SELECT,INSERT,UPDATE ON TABLE public."Centros" TO cae_app_aprovisionamiento;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ClasificacionesRelevanciaCae" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ClasificacionesRelevanciaCae" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ClasificacionesRuidoDetalleGestion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ClasificacionesRuidoDetalleGestion" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ClasificacionesRuidoMensaje" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ClasificacionesRuidoMensaje" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ClavesApi" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ClavesApi" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ConcesionesPrivilegio" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ConcesionesPrivilegio" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ConexionesIntegracion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ConfiguracionesIaDocumentoCliente" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ConfiguracionesIaDocumentoCliente" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ConocimientosDeteccionCampo" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ConocimientosDeteccionCampo" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ContactosAgenda" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ContactosAgenda" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ContactosAgendaRoles" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ContactosAgendaRoles" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ContactosAgendaTiposDocumento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ContactosAgendaTiposDocumento" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ContactosWhatsApp" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ContactosWhatsApp" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Conversaciones" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Conversaciones" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CredencialesAccesoEmpresa" TO cae_app_runtime;
GRANT SELECT ON TABLE public."CredencialesAccesoEmpresa" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CredencialesAccesoSubcontrata" TO cae_app_runtime;
GRANT SELECT ON TABLE public."CredencialesAccesoSubcontrata" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CredencialesIntegracion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."CredencialesIntegracion" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DelegacionesTenant" TO cae_app_runtime;
GRANT SELECT ON TABLE public."DelegacionesTenant" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DetallesSugerenciaGestionCorreo" TO cae_app_runtime;
GRANT SELECT ON TABLE public."DetallesSugerenciaGestionCorreo" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DeteccionesTrabajador" TO cae_app_runtime;
GRANT SELECT ON TABLE public."DeteccionesTrabajador" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Documentos" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Documentos" TO cae_app_soporte;
GRANT SELECT,INSERT ON TABLE public."Documentos" TO cae_app_aprovisionamiento;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DocumentosGenerados" TO cae_app_runtime;
GRANT SELECT ON TABLE public."DocumentosGenerados" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."DominiosProveedorPlataformaCae" TO cae_app_runtime;
GRANT SELECT ON TABLE public."DominiosProveedorPlataformaCae" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Empresas" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Empresas" TO cae_app_soporte;
GRANT SELECT,INSERT,UPDATE ON TABLE public."Empresas" TO cae_app_aprovisionamiento;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."EstadoBootstrapPlataforma" TO cae_app_runtime;
GRANT SELECT ON TABLE public."EstadoBootstrapPlataforma" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."EstadosAutomatizacion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."EstadosAutomatizacion" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."EventosConversacion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."EventosConversacion" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."EventosRecientesUsuario" TO cae_app_runtime;
GRANT SELECT ON TABLE public."EventosRecientesUsuario" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."EventosWebhook" TO cae_app_runtime;
GRANT SELECT ON TABLE public."EventosWebhook" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ExtraccionesIaCache" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ExtraccionesIaCache" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ExtraccionesIaCacheDocumentos" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ExtraccionesIaCacheDocumentos" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FiltrosGuardados" TO cae_app_runtime;
GRANT SELECT ON TABLE public."FiltrosGuardados" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FirmasDigitalesDocumento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."FirmasDigitalesDocumento" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FirmasEnCampoDocumento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."FirmasEnCampoDocumento" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."FirmasGuardadasUsuario" TO cae_app_runtime;
GRANT SELECT ON TABLE public."FirmasGuardadasUsuario" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Gestiones" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Gestiones" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."HistorialImportaciones" TO cae_app_runtime;
GRANT SELECT ON TABLE public."HistorialImportaciones" TO cae_app_soporte;
GRANT SELECT,INSERT ON TABLE public."HistorialImportaciones" TO cae_app_aprovisionamiento;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."HistorialInformes" TO cae_app_runtime;
GRANT SELECT ON TABLE public."HistorialInformes" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Incidencias" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Incidencias" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."IncidenciasPurga" TO cae_app_runtime;
GRANT SELECT ON TABLE public."IncidenciasPurga" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."InstruccionesTratamientoIaTenantPropietario" TO cae_app_runtime;
GRANT SELECT ON TABLE public."InstruccionesTratamientoIaTenantPropietario" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ItemsGeneracionDocumento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ItemsGeneracionDocumento" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LineasWhatsApp" TO cae_app_runtime;
GRANT SELECT ON TABLE public."LineasWhatsApp" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."LotesGeneracionDocumento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."LotesGeneracionDocumento" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."MacrosRespuesta" TO cae_app_runtime;
GRANT SELECT ON TABLE public."MacrosRespuesta" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Mensajes" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Mensajes" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."MiembrosPoolLinea" TO cae_app_runtime;
GRANT SELECT ON TABLE public."MiembrosPoolLinea" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."NotasInternasConversacion" TO cae_app_runtime;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."NotificacionesUsuario" TO cae_app_runtime;
GRANT SELECT ON TABLE public."NotificacionesUsuario" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."OperacionesImportacion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."OperacionesImportacion" TO cae_app_soporte;
GRANT SELECT,INSERT ON TABLE public."OperacionesImportacion" TO cae_app_aprovisionamiento;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."OrdenMenuLateral" TO cae_app_runtime;
GRANT SELECT ON TABLE public."OrdenMenuLateral" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ParametrosSistema" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ParametrosSistema" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ParticipantesConversacion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ParticipantesConversacion" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."PasosTareaAsistente" TO cae_app_runtime;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."PlantillasDocumento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."PlantillasDocumento" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."PlantillasDocumentoVersion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."PlantillasDocumentoVersion" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."PlantillasElemento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."PlantillasElemento" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."PreferenciasDashboardUsuario" TO cae_app_runtime;
GRANT SELECT ON TABLE public."PreferenciasDashboardUsuario" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ProveedoresPlataformaCae" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ProveedoresPlataformaCae" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Proyectos" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Proyectos" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ProyectosTecnicos" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ProyectosTecnicos" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."RechazosAcreditacionDocumentoPlataforma" TO cae_app_runtime;
GRANT SELECT ON TABLE public."RechazosAcreditacionDocumentoPlataforma" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ReclamacionesBuzonIntegracion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ReclamacionesBuzonIntegracion" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ReclamacionesDocumentales" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ReclamacionesDocumentales" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ReclamacionesDocumentalesDocumentos" TO cae_app_runtime;
GRANT SELECT ON TABLE public."ReclamacionesDocumentalesDocumentos" TO cae_app_soporte;

GRANT SELECT,INSERT ON TABLE public."RegistrosAccesoDocumentoSensible" TO cae_app_runtime;
GRANT SELECT ON TABLE public."RegistrosAccesoDocumentoSensible" TO cae_app_soporte;

GRANT SELECT,INSERT ON TABLE public."RegistrosActividadSoporte" TO cae_app_runtime;
GRANT SELECT ON TABLE public."RegistrosActividadSoporte" TO cae_app_soporte;

GRANT SELECT,INSERT ON TABLE public."RegistrosAuditoria" TO cae_app_runtime;
GRANT SELECT ON TABLE public."RegistrosAuditoria" TO cae_app_soporte;
GRANT SELECT,INSERT ON TABLE public."RegistrosAuditoria" TO cae_app_aprovisionamiento;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."RegistrosTiempoGestion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."RegistrosTiempoGestion" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."RelacionesEmpresariales" TO cae_app_runtime;
GRANT SELECT ON TABLE public."RelacionesEmpresariales" TO cae_app_soporte;
GRANT SELECT,INSERT,UPDATE ON TABLE public."RelacionesEmpresariales" TO cae_app_aprovisionamiento;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."RevisionesIaDocumento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."RevisionesIaDocumento" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SellosEmpresa" TO cae_app_runtime;
GRANT SELECT ON TABLE public."SellosEmpresa" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SesionesPrivilegiadas" TO cae_app_runtime;
GRANT SELECT ON TABLE public."SesionesPrivilegiadas" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SolicitudesCertificacionTgss" TO cae_app_runtime;
GRANT SELECT ON TABLE public."SolicitudesCertificacionTgss" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SolicitudesConexionMicrosoft365" TO cae_app_runtime;
GRANT SELECT ON TABLE public."SolicitudesConexionMicrosoft365" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SolicitudesIncorporacionCartera" TO cae_app_runtime;
GRANT SELECT ON TABLE public."SolicitudesIncorporacionCartera" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SolicitudesPrioridadDocumento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."SolicitudesPrioridadDocumento" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SolicitudesPurga" TO cae_app_runtime;
GRANT SELECT ON TABLE public."SolicitudesPurga" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SugerenciasGestionCorreo" TO cae_app_runtime;
GRANT SELECT ON TABLE public."SugerenciasGestionCorreo" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SugerenciasVisitaCorreo" TO cae_app_runtime;
GRANT SELECT ON TABLE public."SugerenciasVisitaCorreo" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."SuscripcionesWebhook" TO cae_app_runtime;
GRANT SELECT ON TABLE public."SuscripcionesWebhook" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."TareasAsistente" TO cae_app_runtime;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."TarifasCliente" TO cae_app_runtime;
GRANT SELECT ON TABLE public."TarifasCliente" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Tenants" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Tenants" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."TenantsAlcanzadosPorConcesion" TO cae_app_runtime;
GRANT SELECT ON TABLE public."TenantsAlcanzadosPorConcesion" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."TiposDocumento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."TiposDocumento" TO cae_app_soporte;
GRANT SELECT,INSERT ON TABLE public."TiposDocumento" TO cae_app_aprovisionamiento;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."TiposDocumentoAlias" TO cae_app_runtime;
GRANT SELECT ON TABLE public."TiposDocumentoAlias" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."TiposDocumentoCentros" TO cae_app_runtime;
GRANT SELECT ON TABLE public."TiposDocumentoCentros" TO cae_app_soporte;
GRANT SELECT,INSERT ON TABLE public."TiposDocumentoCentros" TO cae_app_aprovisionamiento;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Trabajadores" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Trabajadores" TO cae_app_soporte;
GRANT SELECT,INSERT,UPDATE ON TABLE public."Trabajadores" TO cae_app_aprovisionamiento;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."TrabajosAnalisisDocumento" TO cae_app_runtime;
GRANT SELECT ON TABLE public."TrabajosAnalisisDocumento" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."TurnosTareaAsistente" TO cae_app_runtime;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."UltimosResumenesNotificacionPlataforma" TO cae_app_runtime;
GRANT SELECT ON TABLE public."UltimosResumenesNotificacionPlataforma" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Vehiculos" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Vehiculos" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."VerificacionesDocumentoOficial" TO cae_app_runtime;
GRANT SELECT ON TABLE public."VerificacionesDocumentoOficial" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."VerificacionesExternaSubcontrata" TO cae_app_runtime;
GRANT SELECT ON TABLE public."VerificacionesExternaSubcontrata" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."Visitas" TO cae_app_runtime;
GRANT SELECT ON TABLE public."Visitas" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."VisitasTrabajadores" TO cae_app_runtime;
GRANT SELECT ON TABLE public."VisitasTrabajadores" TO cae_app_soporte;

GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."__EFMigrationsHistory" TO cae_app_runtime;
GRANT SELECT ON TABLE public."__EFMigrationsHistory" TO cae_app_soporte;

ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT,USAGE ON SEQUENCES TO cae_app_runtime;

ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT,INSERT,DELETE,UPDATE ON TABLES TO cae_app_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO cae_app_soporte;

SET LOCAL check_function_bodies = true;
""";
    }
}
