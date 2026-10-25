using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// RLS sobre <c>DelegacionesTenant</c>, que hasta aquí no tenía ninguna: cualquier sesión de
/// <c>cae_app_runtime</c> o <c>cae_app_soporte</c> leía los vínculos de todos los Tenants.
///
/// <para>
/// La fila enlaza dos Tenants (<c>TenantConsultoraId</c>, nombre legacy del Tenant del Operador
/// CAE o de la plataforma en soporte, y <c>TenantClienteId</c>, nombre legacy del Tenant
/// propietario destinatario), así que no lleva <c>aislamiento_tenant</c> sino dos políticas:
/// <list type="bullet">
/// <item><c>extremos_de_la_delegacion</c> (todas las órdenes, USING y WITH CHECK): el Tenant
/// activo es uno de los dos extremos. Es la única que gobierna INSERT, UPDATE y DELETE.</item>
/// <item><c>organizacion_de_origen_lee_sus_delegaciones</c> (solo SELECT): el Tenant de origen de
/// la sesión es uno de los dos extremos. Imprescindible: dentro de un workspace delegado el Tenant
/// activo es el del propietario, y el selector de Tenant beneficiario, <c>/cuenta/cliente-activo</c>
/// y el logo de otro Tenant necesitan las demás delegaciones del propio Operador CAE. Va aparte,
/// y no como un OR más de la primera, porque en una política de todas las órdenes el USING decide
/// también qué filas se borran o se reapuntan: el Tenant de origen da lectura, nunca escritura.</item>
/// </list>
/// </para>
///
/// <para>
/// Sin FORCE a propósito, como <c>AsignacionesOperacion</c>: el backfill de asignaciones y la
/// reconciliación de soporte del arranque, y la retirada de un Tenant de demo, leen el catálogo
/// entero con la identidad de bootstrap (rol propietario). Ver <c>TablasConRlsSinForce</c> en
/// <c>CoberturaRlsDelModeloTests</c>. No se amplía ningún GRANT ni se añade excepción por rol.
/// </para>
/// </summary>
public partial class ProtegeDelegacionesTenantConRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public."DelegacionesTenant" ENABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS extremos_de_la_delegacion ON public."DelegacionesTenant";
            DROP POLICY IF EXISTS organizacion_de_origen_lee_sus_delegaciones ON public."DelegacionesTenant";
            CREATE POLICY extremos_de_la_delegacion ON public."DelegacionesTenant"
                USING ("TenantConsultoraId" = (SELECT public.app_ctx_tenant_id())
                    OR "TenantClienteId" = (SELECT public.app_ctx_tenant_id()))
                WITH CHECK ("TenantConsultoraId" = (SELECT public.app_ctx_tenant_id())
                    OR "TenantClienteId" = (SELECT public.app_ctx_tenant_id()));
            CREATE POLICY organizacion_de_origen_lee_sus_delegaciones ON public."DelegacionesTenant"
                FOR SELECT
                USING ("TenantConsultoraId" = (SELECT public.app_ctx_tenant_origen_id())
                    OR "TenantClienteId" = (SELECT public.app_ctx_tenant_origen_id()));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS organizacion_de_origen_lee_sus_delegaciones ON public."DelegacionesTenant";
            DROP POLICY IF EXISTS extremos_de_la_delegacion ON public."DelegacionesTenant";
            ALTER TABLE public."DelegacionesTenant" DISABLE ROW LEVEL SECURITY;
            """);
    }
}
