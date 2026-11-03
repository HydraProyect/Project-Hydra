using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeEnviosAvisoIncidenteSeguridad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EnviosAvisoIncidenteSeguridad",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidenteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Plantilla = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TenantAfectadoId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinatarioNombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DestinatarioCorreo = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SinContactoValidado = table.Column<bool>(type: "boolean", nullable: false),
                    Asunto = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Cuerpo = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    ConfirmadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfirmadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Intentos = table.Column<int>(type: "integer", nullable: false),
                    UltimoIntentoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UltimoIntentoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnviadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UltimoError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnviosAvisoIncidenteSeguridad", x => x.Id);
                    table.CheckConstraint("CK_EnviosAvisoIncidenteSeguridad_EnviadoConHora", "(\"Estado\" = 'Enviado') = (\"EnviadoEnUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_EnviosAvisoIncidenteSeguridad_Estado", "\"Estado\" IN ('Enviando', 'Enviado', 'Fallido')");
                    table.CheckConstraint("CK_EnviosAvisoIncidenteSeguridad_Intentos", "\"Intentos\" >= 1");
                    table.CheckConstraint("CK_EnviosAvisoIncidenteSeguridad_Plantilla", "\"Plantilla\" IN ('PrimerAviso', 'NotificacionCompleta')");
                    table.ForeignKey(
                        name: "FK_EnviosAvisoIncidenteSeguridad_IncidentesSeguridadSospechado~",
                        column: x => x.IncidenteId,
                        principalTable: "IncidentesSeguridadSospechados",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnviosAvisoIncidenteSeguridad_IncidenteId_Plantilla_TenantA~",
                table: "EnviosAvisoIncidenteSeguridad",
                columns: new[] { "IncidenteId", "Plantilla", "TenantAfectadoId", "DestinatarioCorreo" },
                unique: true);

            // Aviso de violaciones de seguridad, incremento 3b: el Actor de Plataforma TALVEG decide sobre el
            // incidente y envía el primer aviso y la notificación completa a los Tenants afectados.
            //
            // EnviosAvisoIncidenteSeguridad es del plano de Plataforma, como su incidente: sin TenantId (el Tenant
            // afectado es el dato TenantAfectadoId), RLS + FORCE y una sola política, de lectura,
            // actor_de_plataforma_lee (concesión AdminPlataforma global vigente sobre app_ctx_usuario_id()).
            // cae_app_runtime solo tiene SELECT; soporte y aprovisionamiento, nada. Ningún rol de la aplicación
            // escribe en las tres tablas del incidente: las tres funciones de abajo son la única puerta.
            //
            // Las tres son SECURITY DEFINER con search_path fijado, mismo régimen que
            // app_resolver_contacto_seguridad, y empiezan por app_actor_plataforma_aviso_seguridad(): contexto RLS
            // firmado válido, concesión AdminPlataforma global vigente y cuenta activa de un Tenant de plataforma
            // trabajando desde su propio Tenant (no dentro de otro Tenant ni en una Sesión Privilegiada sobre él).
            // Cuenta activa con el umbral de ApplicationUser.UmbralDeCuentaDesactivada, como
            // app_cuenta_firmada_vigente: la concesión no caduca al desactivar la cuenta, y el circuito abierto
            // sigue vivo hasta la siguiente revalidación.
            // Esa ayudante no se concede a nadie: solo la llaman las otras tres, que corren como su propietario.
            //
            // - app_decidir_incidente_seguridad: Evaluar (Sospechado → EnEvaluacion), Confirmar (Sospechado o
            //   EnEvaluacion → Confirmado, fija ConfirmadoEnUtc, desde la que cuentan las 24 y las 36 horas) y
            //   Descartar (con motivo; nunca un Confirmado). Deja la transición con el actor. Confirmar dos veces
            //   devuelve ya_confirmado sin mover la hora: es el reintento del mismo clic.
            // - app_reclamar_envio_aviso_seguridad: solo para un incidente Confirmado, un Tenant que esté en sus
            //   TenantsAfectados y un destinatario que app_destinatarios_aviso_seguridad (incremento 3a) devuelva
            //   para ese Tenant con la misma marca SinContactoValidado: la aplicación no puede mandar el aviso a un
            //   correo cualquiera. Crea la fila (una por incidente, plantilla, Tenant y correo en minúsculas) o
            //   reclama la existente si falló o lleva más de 10 minutos Enviando (proceso caído), y devuelve el
            //   asunto y el cuerpo guardados: un reintento reenvía el texto de la primera vez y nunca duplica lo
            //   ya Enviado. Solo quien la reclama envía.
            // - app_resolver_envio_aviso_seguridad: el mismo actor que la reclamó la marca Enviada o Fallida; al
            //   enviarse deja una fila de auditoría en el Tenant afectado, sin el correo ni el cuerpo.
            //
            // Como las de los incrementos 1 y 3a, dependen de que su propietario (el rol de las migraciones) sea
            // superusuario o tenga BYPASSRLS; lo comprueban y fallan en vez de callar, y la comprobación final lo
            // exige aquí para que el fallo salga al desplegar y no en el clic de confirmar, con el plazo corriendo.
            // EXECUTE solo para cae_app_runtime.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."EnviosAvisoIncidenteSeguridad" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."EnviosAvisoIncidenteSeguridad" FORCE ROW LEVEL SECURITY;
                CREATE POLICY actor_de_plataforma_lee ON public."EnviosAvisoIncidenteSeguridad"
                    FOR SELECT TO cae_app_runtime
                    USING ((SELECT public.app_es_admin_plataforma_global((SELECT public.app_ctx_usuario_id()))));
                REVOKE ALL ON TABLE public."EnviosAvisoIncidenteSeguridad" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT ON TABLE public."EnviosAvisoIncidenteSeguridad" TO cae_app_runtime;

                CREATE FUNCTION public.app_actor_plataforma_aviso_seguridad() RETURNS uuid
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_tenant_contexto uuid;
                BEGIN
                  IF NOT public.app_ctx_valido() THEN
                    RETURN NULL;
                  END IF;
                  v_actor := public.app_ctx_usuario_id();
                  v_tenant_contexto := public.app_ctx_tenant_id();
                  IF v_actor IS NULL OR v_tenant_contexto IS NULL THEN
                    RETURN NULL;
                  END IF;
                  -- La autoridad es la concesión AdminPlataforma global vigente, no un rol de Identity.
                  IF NOT public.app_es_admin_plataforma_global(v_actor) THEN
                    RETURN NULL;
                  END IF;
                  -- Desde su propio Tenant de plataforma. EsPlataforma aquí solo restringe.
                  IF NOT EXISTS (
                      SELECT 1 FROM public."AspNetUsers" a
                      JOIN public."Tenants" tp ON tp."Id" = a."TenantId"
                      WHERE a."Id" = v_actor AND tp."EsPlataforma" AND a."TenantId" = v_tenant_contexto
                        AND (a."LockoutEnd" IS NULL OR a."LockoutEnd" <= pg_catalog.now() + interval '365 days'))
                  THEN
                    RETURN NULL;
                  END IF;
                  RETURN v_actor;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_actor_plataforma_aviso_seguridad() FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;

                CREATE FUNCTION public.app_decidir_incidente_seguridad(p_incidente uuid, p_decision text, p_motivo text) RETURNS text
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_estado text;
                  v_nuevo text;
                  v_motivo text;
                  v_codigo text;
                  v_ahora timestamp with time zone := now();
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                 WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: las decisiones sobre incidentes de seguridad no podrían escribir', current_user;
                  END IF;

                  v_actor := public.app_actor_plataforma_aviso_seguridad();
                  IF v_actor IS NULL THEN
                    RETURN 'no_autorizado';
                  END IF;
                  IF p_incidente IS NULL OR p_decision IS NULL OR p_decision NOT IN ('Evaluar', 'Confirmar', 'Descartar') THEN
                    RETURN 'estado_no_valido';
                  END IF;

                  SELECT i."Estado" INTO v_estado
                  FROM public."IncidentesSeguridadSospechados" i
                  WHERE i."Id" = p_incidente
                  FOR UPDATE;
                  IF NOT FOUND THEN
                    RETURN 'no_encontrado';
                  END IF;

                  IF p_decision = 'Confirmar' THEN
                    IF v_estado = 'Confirmado' THEN
                      RETURN 'ya_confirmado';
                    END IF;
                    IF v_estado NOT IN ('Sospechado', 'EnEvaluacion') THEN
                      RETURN 'estado_no_valido';
                    END IF;
                    UPDATE public."IncidentesSeguridadSospechados"
                    SET "Estado" = 'Confirmado', "ConfirmadoEnUtc" = v_ahora
                    WHERE "Id" = p_incidente;
                    v_nuevo := 'Confirmado';
                    v_motivo := NULL;
                    v_codigo := 'confirmado';
                  ELSIF p_decision = 'Evaluar' THEN
                    IF v_estado = 'EnEvaluacion' THEN
                      RETURN 'ya_en_evaluacion';
                    END IF;
                    IF v_estado <> 'Sospechado' THEN
                      RETURN 'estado_no_valido';
                    END IF;
                    UPDATE public."IncidentesSeguridadSospechados" SET "Estado" = 'EnEvaluacion' WHERE "Id" = p_incidente;
                    v_nuevo := 'EnEvaluacion';
                    v_motivo := NULL;
                    v_codigo := 'en_evaluacion';
                  ELSE
                    v_motivo := btrim(p_motivo);
                    IF v_motivo IS NULL OR v_motivo = '' OR char_length(v_motivo) > 500 THEN
                      RETURN 'motivo_no_valido';
                    END IF;
                    IF v_estado NOT IN ('Sospechado', 'EnEvaluacion') THEN
                      RETURN 'estado_no_valido';
                    END IF;
                    UPDATE public."IncidentesSeguridadSospechados" SET "Estado" = 'Descartado' WHERE "Id" = p_incidente;
                    v_nuevo := 'Descartado';
                    v_codigo := 'descartado';
                  END IF;

                  INSERT INTO public."TransicionesIncidenteSeguridad"
                    ("Id", "IncidenteId", "EstadoAnterior", "EstadoNuevo", "OcurridoEnUtc", "ActorUsuarioId", "Motivo")
                  VALUES (uuidv7(), p_incidente, v_estado, v_nuevo, v_ahora, v_actor, v_motivo);

                  RETURN v_codigo;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_decidir_incidente_seguridad(uuid, text, text) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_decidir_incidente_seguridad(uuid, text, text) TO cae_app_runtime;

                CREATE FUNCTION public.app_reclamar_envio_aviso_seguridad(
                    p_incidente uuid, p_plantilla text, p_tenant uuid, p_nombre text, p_correo text,
                    p_sin_contacto boolean, p_asunto text, p_cuerpo text)
                    RETURNS TABLE(codigo text, envio_id uuid, reclamado boolean, estado_envio text, asunto_envio text, cuerpo_envio text)
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_estado_incidente text;
                  v_tenants uuid[];
                  v_correo text;
                  v_nombre text;
                  v_id uuid;
                  v_fila record;
                  v_ahora timestamp with time zone := now();
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                 WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: los envíos del aviso de seguridad no podrían escribir', current_user;
                  END IF;

                  v_actor := public.app_actor_plataforma_aviso_seguridad();
                  IF v_actor IS NULL THEN
                    RETURN QUERY SELECT 'no_autorizado'::text, NULL::uuid, false, NULL::text, NULL::text, NULL::text;
                    RETURN;
                  END IF;

                  v_correo := lower(btrim(p_correo));
                  v_nombre := btrim(p_nombre);
                  IF p_incidente IS NULL OR p_tenant IS NULL OR p_sin_contacto IS NULL
                     OR p_plantilla IS NULL OR p_plantilla NOT IN ('PrimerAviso', 'NotificacionCompleta')
                     OR v_correo IS NULL OR v_correo = '' OR char_length(v_correo) > 256
                     OR v_nombre IS NULL OR v_nombre = '' OR char_length(v_nombre) > 200
                     OR p_asunto IS NULL OR btrim(p_asunto) = '' OR char_length(p_asunto) > 300
                     OR p_cuerpo IS NULL OR btrim(p_cuerpo) = '' OR char_length(p_cuerpo) > 20000 THEN
                    RETURN QUERY SELECT 'contenido_no_valido'::text, NULL::uuid, false, NULL::text, NULL::text, NULL::text;
                    RETURN;
                  END IF;

                  SELECT i."Estado", i."TenantsAfectados" INTO v_estado_incidente, v_tenants
                  FROM public."IncidentesSeguridadSospechados" i
                  WHERE i."Id" = p_incidente;
                  IF NOT FOUND THEN
                    RETURN QUERY SELECT 'no_encontrado'::text, NULL::uuid, false, NULL::text, NULL::text, NULL::text;
                    RETURN;
                  END IF;
                  -- Nunca se envía solo: sin la confirmación del Actor de Plataforma TALVEG no hay envío.
                  IF v_estado_incidente <> 'Confirmado' THEN
                    RETURN QUERY SELECT 'no_confirmado'::text, NULL::uuid, false, NULL::text, NULL::text, NULL::text;
                    RETURN;
                  END IF;
                  -- Solo a un Tenant afectado y a un destinatario que la base resuelve para él.
                  IF (p_tenant = ANY (v_tenants)) IS NOT TRUE
                     OR NOT EXISTS (
                         SELECT 1 FROM public.app_destinatarios_aviso_seguridad(ARRAY[p_tenant]) d
                         WHERE d."TenantId" = p_tenant AND lower(btrim(d."Correo")) = v_correo
                           AND d."SinContactoValidado" = p_sin_contacto) THEN
                    RETURN QUERY SELECT 'destinatario_no_valido'::text, NULL::uuid, false, NULL::text, NULL::text, NULL::text;
                    RETURN;
                  END IF;

                  INSERT INTO public."EnviosAvisoIncidenteSeguridad"
                    ("Id", "IncidenteId", "Plantilla", "TenantAfectadoId", "DestinatarioNombre", "DestinatarioCorreo",
                     "SinContactoValidado", "Asunto", "Cuerpo", "ConfirmadoPorUsuarioId", "ConfirmadoEnUtc", "Estado",
                     "Intentos", "UltimoIntentoEnUtc", "UltimoIntentoPorUsuarioId", "EnviadoEnUtc", "UltimoError")
                  VALUES
                    (uuidv7(), p_incidente, p_plantilla, p_tenant, v_nombre, v_correo, p_sin_contacto, p_asunto, p_cuerpo,
                     v_actor, v_ahora, 'Enviando', 1, v_ahora, v_actor, NULL, NULL)
                  ON CONFLICT ("IncidenteId", "Plantilla", "TenantAfectadoId", "DestinatarioCorreo") DO NOTHING
                  RETURNING "Id" INTO v_id;
                  IF v_id IS NOT NULL THEN
                    RETURN QUERY SELECT 'ok'::text, v_id, true, 'Enviando'::text, p_asunto, p_cuerpo;
                    RETURN;
                  END IF;

                  SELECT e."Id" AS id, e."Estado" AS estado, e."UltimoIntentoEnUtc" AS ultimo,
                         e."Asunto" AS asunto, e."Cuerpo" AS cuerpo
                  INTO v_fila
                  FROM public."EnviosAvisoIncidenteSeguridad" e
                  WHERE e."IncidenteId" = p_incidente AND e."Plantilla" = p_plantilla
                    AND e."TenantAfectadoId" = p_tenant AND e."DestinatarioCorreo" = v_correo
                  FOR UPDATE;

                  IF v_fila.estado = 'Enviado'
                     OR (v_fila.estado = 'Enviando' AND v_fila.ultimo > v_ahora - interval '10 minutes') THEN
                    RETURN QUERY SELECT 'ok'::text, v_fila.id, false, v_fila.estado::text, v_fila.asunto::text, v_fila.cuerpo::text;
                    RETURN;
                  END IF;

                  UPDATE public."EnviosAvisoIncidenteSeguridad"
                  SET "Estado" = 'Enviando', "Intentos" = "Intentos" + 1, "UltimoIntentoEnUtc" = v_ahora,
                      "UltimoIntentoPorUsuarioId" = v_actor, "UltimoError" = NULL
                  WHERE "Id" = v_fila.id;
                  RETURN QUERY SELECT 'ok'::text, v_fila.id, true, 'Enviando'::text, v_fila.asunto::text, v_fila.cuerpo::text;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_reclamar_envio_aviso_seguridad(uuid, text, uuid, text, text, boolean, text, text) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_reclamar_envio_aviso_seguridad(uuid, text, uuid, text, text, boolean, text, text) TO cae_app_runtime;

                CREATE FUNCTION public.app_resolver_envio_aviso_seguridad(p_envio uuid, p_enviado boolean, p_error text) RETURNS text
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_fila record;
                  v_ahora timestamp with time zone := now();
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                 WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: los envíos del aviso de seguridad no podrían escribir', current_user;
                  END IF;

                  v_actor := public.app_actor_plataforma_aviso_seguridad();
                  IF v_actor IS NULL OR p_envio IS NULL OR p_enviado IS NULL THEN
                    RETURN 'no_autorizado';
                  END IF;

                  SELECT e."Estado" AS estado, e."TenantAfectadoId" AS tenant, e."IncidenteId" AS incidente,
                         e."Plantilla" AS plantilla, e."SinContactoValidado" AS sin_contacto,
                         e."ConfirmadoPorUsuarioId" AS confirmado_por, e."UltimoIntentoPorUsuarioId" AS intento_por
                  INTO v_fila
                  FROM public."EnviosAvisoIncidenteSeguridad" e
                  WHERE e."Id" = p_envio
                  FOR UPDATE;
                  IF NOT FOUND THEN
                    RETURN 'no_encontrado';
                  END IF;
                  -- Solo quien lo reclamó lo resuelve, y solo mientras está en curso.
                  IF v_fila.estado <> 'Enviando' OR v_fila.intento_por <> v_actor THEN
                    RETURN 'no_enviando';
                  END IF;

                  IF p_enviado THEN
                    UPDATE public."EnviosAvisoIncidenteSeguridad"
                    SET "Estado" = 'Enviado', "EnviadoEnUtc" = v_ahora, "UltimoError" = NULL
                    WHERE "Id" = p_envio;

                    -- Auditoría en el Tenant afectado, sin el correo ni el cuerpo del aviso.
                    INSERT INTO public."RegistrosAuditoria"
                      ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                       "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                    VALUES
                      (uuidv7(), v_fila.tenant, 'EnvioAvisoIncidenteSeguridad', p_envio, 'Creado', NULL,
                       json_build_object('IncidenteId', v_fila.incidente, 'Plantilla', v_fila.plantilla,
                                         'SinContactoValidado', v_fila.sin_contacto,
                                         'ConfirmadoPorUsuarioId', v_fila.confirmado_por, 'EnviadoEnUtc', v_ahora)::text,
                       v_actor, v_actor, 'Normal', NULL, 'Persona', v_ahora);
                    RETURN 'enviado';
                  END IF;

                  UPDATE public."EnviosAvisoIncidenteSeguridad"
                  SET "Estado" = 'Fallido', "UltimoError" = left(COALESCE(NULLIF(btrim(p_error), ''), 'error_desconocido'), 500)
                  WHERE "Id" = p_envio;
                  RETURN 'fallido';
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_resolver_envio_aviso_seguridad(uuid, boolean, text) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_resolver_envio_aviso_seguridad(uuid, boolean, text) TO cae_app_runtime;

                DO $comprobacion$
                DECLARE
                  v_incumplen text;
                BEGIN
                  SELECT string_agg(p.oid::regprocedure::text, ', ') INTO v_incumplen
                  FROM pg_catalog.pg_proc p
                  JOIN pg_catalog.pg_roles r ON r.oid = p.proowner
                  WHERE p.oid IN ('public.app_actor_plataforma_aviso_seguridad()'::regprocedure,
                                  'public.app_decidir_incidente_seguridad(uuid, text, text)'::regprocedure,
                                  'public.app_reclamar_envio_aviso_seguridad(uuid, text, uuid, text, text, boolean, text, text)'::regprocedure,
                                  'public.app_resolver_envio_aviso_seguridad(uuid, boolean, text)'::regprocedure)
                    AND NOT (r.rolsuper OR r.rolbypassrls);
                  IF v_incumplen IS NOT NULL THEN
                    RAISE EXCEPTION 'Aviso de seguridad: el propietario de % no evita la RLS y el envío fallaría', v_incumplen;
                  END IF;
                END
                $comprobacion$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lo enviado es la documentación de la violación (art. 33.5 RGPD): revertir no la descarta en silencio.
            migrationBuilder.Sql(
                """
                DO $evidencia$ BEGIN
                  IF EXISTS (SELECT 1 FROM public."EnviosAvisoIncidenteSeguridad") THEN
                    RAISE EXCEPTION 'No se puede eliminar la evidencia de los avisos de seguridad enviados';
                  END IF;
                END $evidencia$;
                DROP FUNCTION public.app_resolver_envio_aviso_seguridad(uuid, boolean, text);
                DROP FUNCTION public.app_reclamar_envio_aviso_seguridad(uuid, text, uuid, text, text, boolean, text, text);
                DROP FUNCTION public.app_decidir_incidente_seguridad(uuid, text, text);
                DROP FUNCTION public.app_actor_plataforma_aviso_seguridad();
                """);

            migrationBuilder.DropTable(
                name: "EnviosAvisoIncidenteSeguridad");
        }
    }
}
