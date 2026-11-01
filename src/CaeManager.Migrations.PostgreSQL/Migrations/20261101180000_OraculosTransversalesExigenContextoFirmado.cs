using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Hallazgo 3 de la auditoría de funciones definidoras (ASVS 4.0.3 V4.1.3 y V4.2.1): las resoluciones
    /// transversales de cuenta, webhook y Actor de Plataforma TALVEG respondían a cualquiera con la credencial SQL de
    /// <c>cae_app_runtime</c>, sin contexto. Se usan antes de conocer el Tenant, así que no pueden filtrar por Tenant,
    /// pero la aplicación firma el contexto en toda conexión, también sin Tenant ni usuario, y esa firma exige la clave
    /// protegida fuera de la base: <c>app_exigir_contexto_firmado</c> la pide a todas. No sustituye a RLS ni la toca: no
    /// aísla de una inyección SQL por una conexión de la aplicación, que hereda el token.
    ///
    /// <para>
    /// Las exime solo una sesión cuyo <c>session_user</c> ya evita RLS, como el arranque y la siembra, que no ganan
    /// nada con estas funciones. Mira <c>session_user</c>, no <c>current_user</c>: dentro de una función definidora
    /// <c>current_user</c> es el propietario y eximiría a todos. La guardia no es definidora y nadie más la ejecuta.
    /// </para>
    ///
    /// <para>
    /// Lo demás depende de quién llama. Los webhooks entran anónimos: con un Tenant en el contexto no resuelven. El
    /// Actor que exporta solo se comprueba como lo hace la CLI, con el Tenant exportado y sin usuario, porque antes
    /// aceptaba cualquier correo y cualquier Tenant. La unicidad global de la cuenta pasa a
    /// <c>app_nombre_en_uso_por_otra_cuenta</c> y <c>app_correo_en_uso_por_otra_cuenta</c>, que dan un booleano.
    /// </para>
    ///
    /// <para>
    /// Relevo azul/verde: la versión anterior ya firma y sigue leyendo <c>cuenta_id</c>, así que las dos búsquedas de
    /// cuenta conservan su forma y siguen respondiendo con Tenant en el contexto. Quitar ese dato y esa respuesta es
    /// otra migración, después de que esta versión esté en producción. <c>CREATE OR REPLACE</c> conserva las ACL.
    /// </para>
    /// </summary>
    public partial class OraculosTransversalesExigenContextoFirmado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION public.app_exigir_contexto_firmado() RETURNS void
                    LANGUAGE plpgsql STABLE
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                WHERE rolname = session_user AND (rolsuper OR rolbypassrls)) THEN
                        RETURN;
                    END IF;
                    IF COALESCE(public.app_ctx_valido(), false) THEN
                        RETURN;
                    END IF;
                    RAISE EXCEPTION 'Esta resolución entre Tenants exige contexto RLS firmado' USING ERRCODE = '42501';
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_exigir_contexto_firmado() FROM PUBLIC;

                CREATE OR REPLACE FUNCTION public.app_cuenta_por_nombre_normalizado(nombre_normalizado text)
                    RETURNS TABLE(cuenta_id uuid, tenant_id uuid)
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    RETURN QUERY
                      SELECT u."Id", u."TenantId" FROM public."AspNetUsers" u
                      WHERE u."NormalizedUserName" = nombre_normalizado;
                END;
                $$;

                CREATE OR REPLACE FUNCTION public.app_cuentas_por_email_normalizado(email_normalizado text)
                    RETURNS TABLE(cuenta_id uuid, tenant_id uuid)
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    RETURN QUERY
                      SELECT u."Id", u."TenantId" FROM public."AspNetUsers" u
                      WHERE u."NormalizedEmail" = email_normalizado;
                END;
                $$;

                CREATE OR REPLACE FUNCTION public.app_tenant_de_cuenta(cuenta_id uuid) RETURNS uuid
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    RETURN (SELECT u."TenantId" FROM public."AspNetUsers" u WHERE u."Id" = cuenta_id);
                END;
                $$;

                CREATE OR REPLACE FUNCTION public.app_tenant_de_webhook_graph(p_conexion uuid) RETURNS uuid
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    IF public.app_ctx_tenant_id() IS NOT NULL THEN
                        RETURN NULL;
                    END IF;
                    RETURN (SELECT CASE WHEN count(*) = 1 THEN min(s."TenantId"::text)::uuid ELSE NULL END
                            FROM public."SuscripcionesWebhook" s WHERE s."ConexionIntegracionId" = p_conexion);
                END;
                $$;

                CREATE OR REPLACE FUNCTION public.app_tenant_de_webhook_whatsapp(p_phone_number_id text) RETURNS uuid
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    IF public.app_ctx_tenant_id() IS NOT NULL THEN
                        RETURN NULL;
                    END IF;
                    RETURN (SELECT l."TenantId" FROM public."LineasWhatsApp" l WHERE l."PhoneNumberId" = p_phone_number_id);
                END;
                $$;

                CREATE OR REPLACE FUNCTION public.app_actor_plataforma_que_exporta(p_email_normalizado text, p_tenant uuid)
                    RETURNS TABLE(cuenta_id uuid)
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    IF public.app_ctx_usuario_id() IS NOT NULL OR public.app_ctx_tenant_id() IS DISTINCT FROM p_tenant THEN
                        RAISE EXCEPTION 'El Actor que exporta solo se comprueba en el contexto sin usuario del Tenant exportado' USING ERRCODE = '42501';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la comprobación de la cuenta no vería ninguna fila', current_user;
                    END IF;
                    RETURN QUERY
                      SELECT u."Id"
                      FROM public."AspNetUsers" u
                      JOIN public."Tenants" t ON t."Id" = u."TenantId"
                      WHERE u."NormalizedEmail" = p_email_normalizado
                        AND t."EsPlataforma"
                        AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= pg_catalog.now() + interval '365 days')
                        AND public.app_es_admin_plataforma_sobre(u."Id", p_tenant);
                END;
                $$;

                CREATE FUNCTION public.app_nombre_en_uso_por_otra_cuenta(p_nombre_normalizado text, p_cuenta uuid) RETURNS boolean
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    RETURN EXISTS (SELECT 1 FROM public."AspNetUsers" u
                                    WHERE u."NormalizedUserName" = p_nombre_normalizado AND u."Id" <> p_cuenta);
                END;
                $$;

                CREATE FUNCTION public.app_correo_en_uso_por_otra_cuenta(p_email_normalizado text, p_cuenta uuid) RETURNS boolean
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    RETURN EXISTS (SELECT 1 FROM public."AspNetUsers" u
                                    WHERE u."NormalizedEmail" = p_email_normalizado AND u."Id" <> p_cuenta);
                END;
                $$;

                REVOKE ALL ON FUNCTION public.app_nombre_en_uso_por_otra_cuenta(text, uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_nombre_en_uso_por_otra_cuenta(text, uuid) TO cae_app_runtime;
                REVOKE ALL ON FUNCTION public.app_correo_en_uso_por_otra_cuenta(text, uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_correo_en_uso_por_otra_cuenta(text, uuid) TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.app_cuenta_por_nombre_normalizado(nombre_normalizado text)
                    RETURNS TABLE(cuenta_id uuid, tenant_id uuid)
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT u."Id", u."TenantId" FROM public."AspNetUsers" u WHERE u."NormalizedUserName" = nombre_normalizado;
                $$;

                CREATE OR REPLACE FUNCTION public.app_cuentas_por_email_normalizado(email_normalizado text)
                    RETURNS TABLE(cuenta_id uuid, tenant_id uuid)
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT u."Id", u."TenantId" FROM public."AspNetUsers" u WHERE u."NormalizedEmail" = email_normalizado;
                $$;

                CREATE OR REPLACE FUNCTION public.app_tenant_de_cuenta(cuenta_id uuid) RETURNS uuid
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT u."TenantId" FROM public."AspNetUsers" u WHERE u."Id" = cuenta_id;
                $$;

                CREATE OR REPLACE FUNCTION public.app_tenant_de_webhook_graph(p_conexion uuid) RETURNS uuid
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT CASE WHEN count(*) = 1 THEN min(s."TenantId"::text)::uuid ELSE NULL END
                  FROM public."SuscripcionesWebhook" s WHERE s."ConexionIntegracionId" = p_conexion;
                $$;

                CREATE OR REPLACE FUNCTION public.app_tenant_de_webhook_whatsapp(p_phone_number_id text) RETURNS uuid
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT l."TenantId" FROM public."LineasWhatsApp" l WHERE l."PhoneNumberId" = p_phone_number_id;
                $$;

                CREATE OR REPLACE FUNCTION public.app_actor_plataforma_que_exporta(p_email_normalizado text, p_tenant uuid)
                    RETURNS TABLE(cuenta_id uuid)
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la comprobación de la cuenta no vería ninguna fila', current_user;
                    END IF;
                    RETURN QUERY
                      SELECT u."Id"
                      FROM public."AspNetUsers" u
                      JOIN public."Tenants" t ON t."Id" = u."TenantId"
                      WHERE u."NormalizedEmail" = p_email_normalizado
                        AND t."EsPlataforma"
                        AND (u."LockoutEnd" IS NULL OR u."LockoutEnd" <= pg_catalog.now() + interval '365 days')
                        AND public.app_es_admin_plataforma_sobre(u."Id", p_tenant);
                END;
                $$;

                DROP FUNCTION public.app_nombre_en_uso_por_otra_cuenta(text, uuid);
                DROP FUNCTION public.app_correo_en_uso_por_otra_cuenta(text, uuid);
                DROP FUNCTION public.app_exigir_contexto_firmado();
                """);
        }
    }
}
