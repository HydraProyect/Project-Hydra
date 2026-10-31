using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// M-36, decisión de Chris del 2026-10-31 sobre <c>--exportar-tenant</c> (ASVS 4.0.3 V4.1.3): quien pide la
    /// exportación completa es un Actor de Plataforma TALVEG, no un Administrador del Tenant propietario.
    /// Comprobarlo exige leer el plano de plataforma, que RLS oculta bajo el ámbito del Tenant exportado.
    /// <c>app_actor_plataforma_que_exporta</c> es ese contrato explícito: ninguna política se ensancha.
    ///
    /// <para>
    /// Devuelve solo el identificador, si la cuenta es de un Tenant de plataforma, no está desactivada y
    /// <c>app_es_admin_plataforma_sobre</c> confirma una concesión vigente que alcanza el Tenant pedido.
    /// <c>EsPlataforma</c> aquí solo restringe. Mismo umbral que <c>ApplicationUser.UmbralDeCuentaDesactivada</c>,
    /// como <c>app_cuenta_firmada_vigente</c>. Corre sin contexto firmado porque la CLI no tiene sesión, así que el
    /// correo es parámetro: no da a <c>cae_app_runtime</c> más de lo que ya le dan
    /// <c>app_cuentas_por_email_normalizado</c> y <c>app_es_admin_plataforma_sobre</c>.
    /// </para>
    ///
    /// <para>
    /// Como <c>app_cuenta_firmada_vigente</c>, su propietario tiene que evitar la RLS: si no, no vería ninguna
    /// fila y diría que la cuenta no existe, así que lo comprueba en vez de callar. <c>EXECUTE</c> solo para
    /// <c>cae_app_runtime</c>, la identidad de la CLI. No concede ni retira privilegios de tabla.
    /// </para>
    /// </summary>
    public partial class ActorPlataformaExportaTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION public.app_actor_plataforma_que_exporta(p_email_normalizado text, p_tenant uuid)
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
                REVOKE ALL ON FUNCTION public.app_actor_plataforma_que_exporta(text, uuid) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_actor_plataforma_que_exporta(text, uuid) TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION public.app_actor_plataforma_que_exporta(text, uuid);
                """);
        }
    }
}
