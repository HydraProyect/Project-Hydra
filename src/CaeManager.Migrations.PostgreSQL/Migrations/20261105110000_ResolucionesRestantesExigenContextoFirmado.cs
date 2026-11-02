using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Tres resoluciones transversales que se quedaron fuera de <c>OraculosTransversalesExigenContextoFirmado</c> y
    /// respondían a cualquiera con la credencial SQL (ASVS 4.0.3 V4.1.3): <c>app_estado_cuenta_emisora</c>, que dice si
    /// una cuenta de otro Tenant existe y hasta cuándo está bloqueada, <c>app_tenant_de_clave_api</c>, que da el Tenant
    /// de un hash, y <c>app_sesion_vigente</c>, que confirma una sesión de cualquier cuenta. Las tres pasan por
    /// <c>app_exigir_contexto_firmado</c>, con su misma excepción y su mismo límite: no aísla de una inyección SQL por
    /// una conexión de la aplicación, que hereda el token.
    ///
    /// <para>
    /// Ninguna filtra por Tenant, porque su uso lo impide: la cuenta emisora es de otra organización que el Tenant de la
    /// clave, la clave resuelve antes de conocer el Tenant y la cookie se revalida antes de conocerlo. Una conexión con
    /// Tenant en el contexto sigue respondiendo, y también la de una Sesión Privilegiada: <c>SET ROLE cae_app_soporte</c>
    /// conserva el <c>session_user</c> y el token, y <c>cae_app_soporte</c> mantiene su <c>EXECUTE</c> sobre las dos que
    /// ya lo tenía. Las pruebas que conectan como propietario y adoptan el rol no miden esto, porque la guardia exime a
    /// su sesión: lo mide <c>OraculosTransversalesBajoRuntimeTests</c>.
    /// </para>
    ///
    /// <para>
    /// Relevo azul/verde: la versión que sigue sirviendo mientras corre el migrador firma toda conexión desde
    /// <c>ContraeContextoRlsFirmado</c> (<c>TenantRlsConnectionInterceptor</c>), así que basta un paso. Firma y resultado
    /// no cambian y <c>CREATE OR REPLACE</c> conserva propietario y ACL. <c>app_tenant_de_clave_api</c> puede pasar a un
    /// <c>RETURN</c> con subconsulta escalar porque <c>IX_ClavesApi_HashClave</c> es único.
    /// </para>
    /// </summary>
    public partial class ResolucionesRestantesExigenContextoFirmado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.app_estado_cuenta_emisora(cuenta_id uuid)
                    RETURNS TABLE(fin_bloqueo timestamp with time zone, claves_validas_desde timestamp with time zone)
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    RETURN QUERY
                      SELECT u."LockoutEnd", u."ClavesApiValidasDesdeUtc" FROM public."AspNetUsers" u WHERE u."Id" = cuenta_id;
                END;
                $$;

                CREATE OR REPLACE FUNCTION public.app_tenant_de_clave_api(hash_clave text) RETURNS uuid
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    RETURN (SELECT c."TenantId" FROM public."ClavesApi" c
                            WHERE c."HashClave" = hash_clave AND c."EstaEliminado" = false);
                END;
                $$;

                CREATE OR REPLACE FUNCTION public.app_sesion_vigente(p_sid uuid, p_usuario uuid) RETURNS boolean
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la comprobación de sesión no vería ninguna fila', current_user;
                    END IF;
                    RETURN EXISTS (SELECT 1 FROM public."SesionesUsuario"
                                    WHERE "Sid" = p_sid AND "UsuarioId" = p_usuario AND "RevocadaEnUtc" IS NULL);
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.app_estado_cuenta_emisora(cuenta_id uuid)
                    RETURNS TABLE(fin_bloqueo timestamp with time zone, claves_validas_desde timestamp with time zone)
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT u."LockoutEnd", u."ClavesApiValidasDesdeUtc" FROM public."AspNetUsers" u WHERE u."Id" = cuenta_id;
                $$;

                CREATE OR REPLACE FUNCTION public.app_tenant_de_clave_api(hash_clave text) RETURNS uuid
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT c."TenantId" FROM public."ClavesApi" c
                  WHERE c."HashClave" = hash_clave AND c."EstaEliminado" = false;
                $$;

                CREATE OR REPLACE FUNCTION public.app_sesion_vigente(p_sid uuid, p_usuario uuid) RETURNS boolean
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la comprobación de sesión no vería ninguna fila', current_user;
                    END IF;
                    RETURN EXISTS (SELECT 1 FROM public."SesionesUsuario"
                                    WHERE "Sid" = p_sid AND "UsuarioId" = p_usuario AND "RevocadaEnUtc" IS NULL);
                END;
                $$;
                """);
        }
    }
}
