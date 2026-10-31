using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// RLS sobre <c>AsignacionesOperadorDelegado</c>, que hasta aquí exponía a cualquier sesión de
/// <c>cae_app_runtime</c> o <c>cae_app_soporte</c> qué personas operan qué delegaciones entre Tenants.
///
/// <para>
/// La fila no lleva Tenant propio: pertenece a la relación entre el Operador CAE externo y el Tenant
/// propietario, y hereda sus dos extremos de la <c>DelegacionesTenant</c> a la que apunta. Las dos
/// políticas repiten las de esa tabla (migración <c>ProtegeDelegacionesTenantConRls</c>), con la misma
/// separación y por el mismo motivo:
/// </para>
/// <list type="bullet">
/// <item><c>extremos_de_la_delegacion</c> (todas las órdenes, USING y WITH CHECK): el Tenant activo es
/// un extremo de la delegación. Sin un extremo a la vista la fila no se escribe.</item>
/// <item><c>organizacion_de_origen_lee_sus_asignaciones</c> (solo SELECT): el Tenant de origen es un
/// extremo. Es la lectura que necesita el usuario del Operador CAE externo dentro del workspace de
/// otro Tenant para mantener el selector y la vía heredada de <c>/cuenta/cliente-activo</c>; da
/// lectura, nunca escritura.</item>
/// </list>
///
/// <para>
/// Sin FORCE, igual que <c>DelegacionesTenant</c>: el backfill de asignaciones del arranque y la
/// retirada de un Tenant de demo leen con la identidad propietaria de las tablas, sin contexto de
/// Tenant. La subconsulta de la política no recurre: las políticas de <c>DelegacionesTenant</c> solo
/// miran columnas propias.
/// </para>
/// </summary>
public partial class ProtegeAsignacionesOperadorDelegadoConRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public."AsignacionesOperadorDelegado" ENABLE ROW LEVEL SECURITY;

            DROP POLICY IF EXISTS organizacion_de_origen_lee_sus_asignaciones ON public."AsignacionesOperadorDelegado";
            DROP POLICY IF EXISTS extremos_de_la_delegacion ON public."AsignacionesOperadorDelegado";

            CREATE POLICY extremos_de_la_delegacion ON public."AsignacionesOperadorDelegado"
                USING (EXISTS (
                    SELECT 1 FROM public."DelegacionesTenant" d
                    WHERE d."Id" = "AsignacionesOperadorDelegado"."DelegacionTenantId"
                      AND (d."TenantConsultoraId" = (SELECT public.app_ctx_tenant_id())
                           OR d."TenantClienteId" = (SELECT public.app_ctx_tenant_id()))))
                WITH CHECK (EXISTS (
                    SELECT 1 FROM public."DelegacionesTenant" d
                    WHERE d."Id" = "AsignacionesOperadorDelegado"."DelegacionTenantId"
                      AND (d."TenantConsultoraId" = (SELECT public.app_ctx_tenant_id())
                           OR d."TenantClienteId" = (SELECT public.app_ctx_tenant_id()))));

            CREATE POLICY organizacion_de_origen_lee_sus_asignaciones ON public."AsignacionesOperadorDelegado"
                FOR SELECT
                USING (EXISTS (
                    SELECT 1 FROM public."DelegacionesTenant" d
                    WHERE d."Id" = "AsignacionesOperadorDelegado"."DelegacionTenantId"
                      AND (d."TenantConsultoraId" = (SELECT public.app_ctx_tenant_origen_id())
                           OR d."TenantClienteId" = (SELECT public.app_ctx_tenant_origen_id()))));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS organizacion_de_origen_lee_sus_asignaciones ON public."AsignacionesOperadorDelegado";
            DROP POLICY IF EXISTS extremos_de_la_delegacion ON public."AsignacionesOperadorDelegado";
            ALTER TABLE public."AsignacionesOperadorDelegado" DISABLE ROW LEVEL SECURITY;
            """);
    }
}
