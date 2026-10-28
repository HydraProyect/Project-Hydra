using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Cuatro ojos en la resolución del contacto de seguridad del Tenant beneficiario (aviso de violaciones de
    /// seguridad, seguimiento del incremento 3a): la cuenta que propuso un contacto no puede validarlo ni
    /// rechazarlo. El caso real es un Actor de Plataforma TALVEG que, como Administrador del Tenant de
    /// plataforma, propone el contacto de ese Tenant y tiene también la concesión AdminPlataforma global.
    ///
    /// <para>
    /// <c>ValidarContactoSeguridadCommand</c> y <c>RechazarContactoSeguridadCommand</c> lo comprueban antes
    /// para fallar pronto con mensaje; <c>app_resolver_contacto_seguridad</c> lo repite aquí, como repite la
    /// concesión, la cuenta de plataforma, el estado y el motivo, y devuelve <c>misma_cuenta</c> sin escribir.
    /// Se compara <c>PropuestoPorUsuarioId</c> con <c>app_ctx_usuario_id()</c>, la identidad del contexto RLS
    /// firmado, que es la misma que la política de alta exige a quien propone y la que la función registra
    /// como actor en la auditoría.
    /// </para>
    ///
    /// <para>
    /// Solo cambia el cuerpo de la función (<c>CREATE OR REPLACE</c>, misma firma): propietario, GRANT y
    /// REVOKE se conservan. Down vuelve al cuerpo de <c>20261027073902_AnadeContactosSeguridadTenant</c>.
    /// </para>
    /// </summary>
    public partial class ContactoSeguridadCuatroOjos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.app_resolver_contacto_seguridad(p_contacto uuid, p_validar boolean, p_motivo text) RETURNS text
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                  v_actor uuid;
                  v_tenant_contexto uuid;
                  v_tenant uuid;
                  v_estado text;
                  v_propuesto_por uuid;
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

                  SELECT c."TenantId", c."Estado", c."PropuestoPorUsuarioId" INTO v_tenant, v_estado, v_propuesto_por
                  FROM public."ContactosSeguridadTenant" c
                  WHERE c."Id" = p_contacto
                  FOR UPDATE;
                  IF NOT FOUND THEN
                    RETURN 'no_encontrado';
                  END IF;
                  IF v_estado <> 'Propuesto' THEN
                    RETURN 'no_propuesto';
                  END IF;

                  -- Cuatro ojos: la cuenta que propuso el contacto no lo valida ni lo rechaza.
                  IF v_propuesto_por = v_actor THEN
                    RETURN 'misma_cuenta';
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public.app_resolver_contacto_seguridad(p_contacto uuid, p_validar boolean, p_motivo text) RETURNS text
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
                """);
        }
    }
}
