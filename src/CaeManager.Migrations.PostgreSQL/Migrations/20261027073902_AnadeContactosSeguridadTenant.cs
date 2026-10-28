using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeContactosSeguridadTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContactosSeguridadTenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Correo = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Telefono = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PropuestoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropuestoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResueltoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResueltoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MotivoRechazo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SustitucionEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactosSeguridadTenant", x => x.Id);
                    table.CheckConstraint("CK_ContactosSeguridadTenant_Estado", "\"Estado\" IN ('Propuesto', 'Validado', 'Rechazado', 'Sustituido')");
                    table.CheckConstraint("CK_ContactosSeguridadTenant_InstanteSustitucion", "(\"Estado\" = 'Sustituido') = (\"SustitucionEnUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_ContactosSeguridadTenant_MotivoSoloAlRechazar", "(\"Estado\" = 'Rechazado') = (\"MotivoRechazo\" IS NOT NULL)");
                    table.CheckConstraint("CK_ContactosSeguridadTenant_ResolucionCoherente", "(\"Estado\" IN ('Validado', 'Rechazado')) = (\"ResueltoPorUsuarioId\" IS NOT NULL AND \"ResueltoEnUtc\" IS NOT NULL) OR (\"Estado\" = 'Sustituido' AND \"ResueltoPorUsuarioId\" IS NOT NULL AND \"ResueltoEnUtc\" IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContactosSeguridadTenant_Estado_PropuestoEnUtc",
                table: "ContactosSeguridadTenant",
                columns: new[] { "Estado", "PropuestoEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactosSeguridadTenant_UnoPropuestoYUnoValidadoPorTenant",
                table: "ContactosSeguridadTenant",
                columns: new[] { "TenantId", "Estado" },
                unique: true,
                filter: "\"Estado\" IN ('Propuesto', 'Validado')");

            // Contacto de seguridad del Tenant beneficiario (aviso de violaciones de seguridad, RGPD art. 33,
            // incremento 3a). Decisión del propietario del 2026-10-26: lo propone el Administrador del Tenant y
            // lo valida un Actor de Plataforma TALVEG; solo un contacto validado recibe avisos.
            //
            // RLS + FORCE, como toda tabla con TenantId, con aislamiento_tenant sobre el contexto RLS firmado y
            // tres políticas más, ninguna de las cuales ensancha lo que el Tenant propietario puede escribir:
            //
            // - solo_el_administrador_propone (RESTRICTIVE, INSERT): el alta solo nace Propuesta, sin resolución,
            //   a nombre de quien firma el contexto y si es Administrador de ese Tenant
            //   (app_es_administrador_del_tenant, la misma función que ya usan otras políticas).
            // - solo_el_administrador_sustituye_la_propuesta (RESTRICTIVE, UPDATE): lo único que el Tenant
            //   cambia es una Propuesta a Sustituida, sin resolución, y además solo puede tocar Estado y
            //   SustitucionEnUtc (GRANT UPDATE por columna). Validar y rechazar quedan fuera de su alcance.
            // - actor_de_plataforma_lee (PERMISSIVE, solo SELECT, cae_app_runtime): concesión AdminPlataforma
            //   global vigente sobre app_ctx_usuario_id(), con app_es_admin_plataforma_global, la misma función
            //   que ya usan las políticas de plataforma. Da lectura, nunca escritura.
            //
            // Validar y rechazar escriben filas de otro Tenant, y ninguna política se abre para eso: lo hace la
            // función SECURITY DEFINER app_resolver_contacto_seguridad, con search_path fijado, mismo régimen que
            // app_revocar_operador_cae_externo_por_soporte. Repite en la base lo que el comando comprueba en C#
            // (contexto firmado válido, concesión AdminPlataforma global vigente, cuenta de un Tenant de
            // plataforma trabajando desde su propio Tenant, fila Propuesta, motivo al rechazar) y deja la
            // auditoría en el Tenant beneficiario en la misma transacción. Se eligió función y no Sesión
            // Privilegiada porque el acto es transversal (la cola de propuestas de todos los Tenants) y no una
            // operación dentro de un Tenant objetivo.
            //
            // app_destinatarios_aviso_seguridad resuelve, para los Tenants afectados por un incidente, el
            // contacto validado o, sin él, los Administradores del Tenant (marca SinContactoValidado). Es
            // SECURITY DEFINER porque los Administradores de otro Tenant están fuera de la RLS de AspNetUsers;
            // solo responde a un Actor de Plataforma TALVEG con concesión AdminPlataforma global vigente y, si
            // no, lanza 42501 en vez de devolver una lista vacía que se leería como «nadie a quien avisar».
            //
            // Como las funciones de 20261023221557_AnadeRegistroIniciosSesion, las dos dependen de que su propietario (el rol de las
            // migraciones) sea superusuario o tenga BYPASSRLS: con FORCE, sin eso no verían las filas. Lo
            // comprueban y fallan en vez de callar.
            //
            // cae_app_runtime: SELECT, INSERT y UPDATE de dos columnas; sin DELETE (el historial se conserva).
            // cae_app_soporte: SELECT (Soporte TALVEG en Sesión Privilegiada sobre el Tenant, como las demás
            // tablas del Tenant). cae_app_aprovisionamiento: nada.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."ContactosSeguridadTenant" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."ContactosSeguridadTenant" FORCE ROW LEVEL SECURITY;

                CREATE POLICY aislamiento_tenant ON public."ContactosSeguridadTenant"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));

                CREATE POLICY solo_el_administrador_propone ON public."ContactosSeguridadTenant"
                    AS RESTRICTIVE FOR INSERT
                    WITH CHECK ("Estado" = 'Propuesto'
                        AND "PropuestoPorUsuarioId" = (SELECT public.app_ctx_usuario_id())
                        AND "ResueltoPorUsuarioId" IS NULL
                        AND "ResueltoEnUtc" IS NULL
                        AND "MotivoRechazo" IS NULL
                        AND "SustitucionEnUtc" IS NULL
                        AND public.app_es_administrador_del_tenant((SELECT public.app_ctx_usuario_id()), "TenantId"));

                CREATE POLICY solo_el_administrador_sustituye_la_propuesta ON public."ContactosSeguridadTenant"
                    AS RESTRICTIVE FOR UPDATE
                    USING ("Estado" = 'Propuesto'
                        AND public.app_es_administrador_del_tenant((SELECT public.app_ctx_usuario_id()), "TenantId"))
                    WITH CHECK ("Estado" = 'Sustituido'
                        AND "SustitucionEnUtc" IS NOT NULL
                        AND "ResueltoPorUsuarioId" IS NULL
                        AND "ResueltoEnUtc" IS NULL
                        AND "MotivoRechazo" IS NULL
                        AND public.app_es_administrador_del_tenant((SELECT public.app_ctx_usuario_id()), "TenantId"));

                CREATE POLICY actor_de_plataforma_lee ON public."ContactosSeguridadTenant"
                    FOR SELECT TO cae_app_runtime
                    USING ((SELECT public.app_es_admin_plataforma_global((SELECT public.app_ctx_usuario_id()))));

                REVOKE ALL ON TABLE public."ContactosSeguridadTenant" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."ContactosSeguridadTenant" TO cae_app_runtime;
                GRANT UPDATE ("Estado", "SustitucionEnUtc") ON TABLE public."ContactosSeguridadTenant" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."ContactosSeguridadTenant" TO cae_app_soporte;

                CREATE FUNCTION public.app_resolver_contacto_seguridad(p_contacto uuid, p_validar boolean, p_motivo text) RETURNS text
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_tenant_contexto uuid;
                  v_tenant uuid;
                  v_estado text;
                  v_motivo text;
                  v_ahora timestamp with time zone := now();
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                 WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: la resolución de contactos de seguridad no podría escribir', current_user;
                  END IF;

                  IF NOT public.app_ctx_valido() THEN
                    RETURN 'contexto_no_valido';
                  END IF;
                  v_actor := public.app_ctx_usuario_id();
                  v_tenant_contexto := public.app_ctx_tenant_id();
                  IF v_actor IS NULL OR v_tenant_contexto IS NULL OR p_contacto IS NULL OR p_validar IS NULL THEN
                    RETURN 'contexto_no_valido';
                  END IF;

                  -- La autoridad es la concesión AdminPlataforma global vigente, no un rol de Identity.
                  IF NOT public.app_es_admin_plataforma_global(v_actor) THEN
                    RETURN 'no_autorizado';
                  END IF;

                  -- Un Actor de Plataforma TALVEG desde su propio Tenant de plataforma, no dentro de otro Tenant
                  -- (ni en una Sesión Privilegiada sobre él). EsPlataforma aquí solo restringe.
                  IF NOT EXISTS (
                      SELECT 1 FROM public."AspNetUsers" a
                      JOIN public."Tenants" tp ON tp."Id" = a."TenantId"
                      WHERE a."Id" = v_actor AND tp."EsPlataforma" AND a."TenantId" = v_tenant_contexto)
                  THEN
                    RETURN 'no_autorizado';
                  END IF;

                  IF NOT p_validar THEN
                    v_motivo := btrim(p_motivo);
                    IF v_motivo IS NULL OR v_motivo = '' OR char_length(v_motivo) > 500 THEN
                      RETURN 'motivo_no_valido';
                    END IF;
                  END IF;

                  SELECT c."TenantId", c."Estado" INTO v_tenant, v_estado
                  FROM public."ContactosSeguridadTenant" c
                  WHERE c."Id" = p_contacto
                  FOR UPDATE;
                  IF NOT FOUND THEN
                    RETURN 'no_encontrado';
                  END IF;
                  IF v_estado <> 'Propuesto' THEN
                    RETURN 'no_propuesto';
                  END IF;

                  IF p_validar THEN
                    -- El vigente deja de serlo antes de que el nuevo ocupe su lugar (índice único parcial).
                    WITH sustituidos AS (
                      UPDATE public."ContactosSeguridadTenant" c
                      SET "Estado" = 'Sustituido', "SustitucionEnUtc" = v_ahora
                      WHERE c."TenantId" = v_tenant AND c."Estado" = 'Validado'
                      RETURNING c."Id")
                    INSERT INTO public."RegistrosAuditoria"
                      ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                       "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                    SELECT uuidv7(), v_tenant, 'ContactoSeguridadTenant', s."Id", 'Modificado',
                           json_build_object('Estado', 'Validado')::text,
                           json_build_object('Estado', 'Sustituido', 'SustitucionEnUtc', v_ahora)::text,
                           v_actor, v_actor, 'Normal', NULL, 'Persona', v_ahora
                    FROM sustituidos s;

                    UPDATE public."ContactosSeguridadTenant"
                    SET "Estado" = 'Validado', "ResueltoPorUsuarioId" = v_actor, "ResueltoEnUtc" = v_ahora
                    WHERE "Id" = p_contacto;
                  ELSE
                    UPDATE public."ContactosSeguridadTenant"
                    SET "Estado" = 'Rechazado', "ResueltoPorUsuarioId" = v_actor, "ResueltoEnUtc" = v_ahora,
                        "MotivoRechazo" = v_motivo
                    WHERE "Id" = p_contacto;
                  END IF;

                  -- Auditoría en el Tenant beneficiario, sin los datos personales del contacto (el
                  -- interceptor de auditoría tampoco los copia).
                  INSERT INTO public."RegistrosAuditoria"
                    ("Id", "TenantId", "EntidadTipo", "EntidadId", "Accion", "DatosAntes", "DatosDespues",
                     "UsuarioId", "ActorRealUsuarioId", "ViaAcceso", "ViaAccesoId", "TipoActor", "FechaUtc")
                  VALUES
                    (uuidv7(), v_tenant, 'ContactoSeguridadTenant', p_contacto, 'Modificado',
                     json_build_object('Estado', 'Propuesto')::text,
                     CASE WHEN p_validar
                       THEN json_build_object('Estado', 'Validado', 'ResueltoPorUsuarioId', v_actor, 'ResueltoEnUtc', v_ahora)::text
                       ELSE json_build_object('Estado', 'Rechazado', 'ResueltoPorUsuarioId', v_actor, 'ResueltoEnUtc', v_ahora,
                                              'MotivoRechazo', v_motivo)::text
                     END,
                     v_actor, v_actor, 'Normal', NULL, 'Persona', v_ahora);

                  RETURN CASE WHEN p_validar THEN 'validado' ELSE 'rechazado' END;
                END;
                $$;

                REVOKE ALL ON FUNCTION public.app_resolver_contacto_seguridad(uuid, boolean, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.app_resolver_contacto_seguridad(uuid, boolean, text) TO cae_app_runtime;

                CREATE FUNCTION public.app_destinatarios_aviso_seguridad(p_tenants uuid[])
                    RETURNS TABLE("TenantId" uuid, "SinContactoValidado" boolean, "Nombre" text, "Correo" text, "Telefono" text)
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                #variable_conflict use_column
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                 WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'El rol % no evita la RLS: los destinatarios del aviso de seguridad no se podrían leer', current_user;
                  END IF;

                  IF NOT public.app_ctx_valido()
                     OR public.app_ctx_usuario_id() IS NULL
                     OR NOT public.app_es_admin_plataforma_global(public.app_ctx_usuario_id()) THEN
                    RAISE EXCEPTION 'Solo un Actor de Plataforma TALVEG con AdminPlataforma global resuelve destinatarios'
                      USING ERRCODE = '42501';
                  END IF;

                  RETURN QUERY
                  WITH tenants AS (SELECT DISTINCT t AS id FROM unnest(p_tenants) AS t WHERE t IS NOT NULL),
                  validados AS (
                    SELECT c."TenantId" AS tenant, c."Nombre"::text AS nombre, c."Correo"::text AS correo,
                           c."Telefono"::text AS telefono
                    FROM public."ContactosSeguridadTenant" c
                    JOIN tenants t ON t.id = c."TenantId"
                    WHERE c."Estado" = 'Validado'),
                  administradores AS (
                    -- Mismo criterio de Administrador que app_es_administrador_del_tenant: rol ADMINISTRADOR en
                    -- su Tenant y cuenta no dada de baja.
                    SELECT u."TenantId" AS tenant, u."NombreCompleto"::text AS nombre, u."Email"::text AS correo,
                           u."PhoneNumber"::text AS telefono
                    FROM public."AspNetUsers" u
                    JOIN tenants t ON t.id = u."TenantId"
                    JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
                    JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
                    WHERE r."NormalizedName" = 'ADMINISTRADOR'
                      AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= now() + interval '365 days')
                      AND u."Email" IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM validados v WHERE v.tenant = u."TenantId"))
                  SELECT v.tenant, false, v.nombre, v.correo, v.telefono FROM validados v
                  UNION ALL
                  SELECT a.tenant, true, a.nombre, a.correo, a.telefono FROM administradores a
                  UNION ALL
                  -- Sin contacto validado ni Administradores: una fila sin destinatario, para que el Tenant no
                  -- desaparezca del resultado sin que nadie lo note.
                  SELECT t.id, true, NULL::text, NULL::text, NULL::text FROM tenants t
                  WHERE NOT EXISTS (SELECT 1 FROM validados v WHERE v.tenant = t.id)
                    AND NOT EXISTS (SELECT 1 FROM administradores a WHERE a.tenant = t.id);
                END;
                $$;

                REVOKE ALL ON FUNCTION public.app_destinatarios_aviso_seguridad(uuid[]) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.app_destinatarios_aviso_seguridad(uuid[]) TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION public.app_destinatarios_aviso_seguridad(uuid[]);
                DROP FUNCTION public.app_resolver_contacto_seguridad(uuid, boolean, text);
                """);

            migrationBuilder.DropTable(
                name: "ContactosSeguridadTenant");
        }
    }
}
