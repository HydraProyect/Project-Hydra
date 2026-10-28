using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Resolución del Tenant propietario de un webhook entrante (Microsoft 365 y WhatsApp) bajo FORCE ROW LEVEL
    /// SECURITY (hallazgo S-C-5). <c>WebhookTenantResolver</c> y <c>WebhookWhatsAppTenantResolver</c> consultaban
    /// <c>SuscripcionesWebhook</c> y <c>LineasWhatsApp</c> con <c>IgnoreQueryFilters()</c> y sin Tenant en el
    /// contexto: eso quita el filtro de EF, no la política, y bajo <c>cae_app_runtime</c> no veían ninguna fila
    /// (401 en Graph y mensajes de WhatsApp perdidos).
    ///
    /// <para>
    /// Mismo patrón que <c>app_tenant_de_clave_api</c> (<see cref="LineaBaseCompactada"/>): funciones
    /// <c>SECURITY DEFINER</c> con <c>search_path</c> fijado, propiedad del migrador, sin <c>EXECUTE</c> para
    /// <c>PUBLIC</c> y concedidas solo a <c>cae_app_runtime</c>. Devuelven únicamente el <c>TenantId</c>: el
    /// secreto (<c>clientState</c>, <c>subscriptionId</c>) y la conexión se leen después dentro del ámbito de ese
    /// Tenant, ya bajo RLS. La de Graph devuelve NULL si la conexión tiene filas en más de un Tenant (el índice
    /// único es por Tenant), en vez de elegir uno por el orden de la consulta; la de WhatsApp se apoya en el
    /// índice único global de <c>PhoneNumberId</c>. No se relaja ninguna política ni se concede BYPASSRLS.
    /// </para>
    /// </summary>
    public partial class ResolucionTenantWebhooksBajoRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION public.app_tenant_de_webhook_graph(p_conexion uuid) RETURNS uuid
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT CASE WHEN count(*) = 1 THEN min(s."TenantId"::text)::uuid ELSE NULL END
                  FROM public."SuscripcionesWebhook" s WHERE s."ConexionIntegracionId" = p_conexion;
                $$;
                CREATE FUNCTION public.app_tenant_de_webhook_whatsapp(p_phone_number_id text) RETURNS uuid
                    LANGUAGE sql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                  SELECT l."TenantId" FROM public."LineasWhatsApp" l WHERE l."PhoneNumberId" = p_phone_number_id;
                $$;
                REVOKE ALL ON FUNCTION public.app_tenant_de_webhook_graph(uuid) FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.app_tenant_de_webhook_whatsapp(text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.app_tenant_de_webhook_graph(uuid) TO cae_app_runtime;
                GRANT EXECUTE ON FUNCTION public.app_tenant_de_webhook_whatsapp(text) TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS public.app_tenant_de_webhook_graph(uuid);
                DROP FUNCTION IF EXISTS public.app_tenant_de_webhook_whatsapp(text);
                """);
        }
    }
}
