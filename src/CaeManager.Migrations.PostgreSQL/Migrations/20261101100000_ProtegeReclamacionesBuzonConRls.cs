using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// RLS con FORCE sobre <c>ReclamacionesBuzonIntegracion</c>, que hasta aquí exponía a cualquier
/// sesión de <c>cae_app_runtime</c> o <c>cae_app_soporte</c> qué buzón ha conectado cada Tenant.
///
/// <para>
/// La tabla se documentó como catálogo global, pero su razón de ser es la unicidad global de
/// <c>BuzonEmail</c>, y esa la impone el índice único, que PostgreSQL comprueba sin mirar las
/// políticas. Ningún consumidor necesita leer una fila ajena: reclamar inserta y traduce el 23505
/// por el nombre del índice, y liberar borra la fila del Tenant propietario de la conexión.
/// La política compara <c>TenantPropietarioId</c>, columna <c>NOT NULL</c>; no hace falta función
/// <c>SECURITY DEFINER</c> ni contrato de cruce entre Tenants.
/// </para>
/// </summary>
public partial class ProtegeReclamacionesBuzonConRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public."ReclamacionesBuzonIntegracion" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE ONLY public."ReclamacionesBuzonIntegracion" FORCE ROW LEVEL SECURITY;
            CREATE POLICY aislamiento_tenant ON public."ReclamacionesBuzonIntegracion"
                USING ("TenantPropietarioId" = (SELECT public.app_ctx_tenant_id()))
                WITH CHECK ("TenantPropietarioId" = (SELECT public.app_ctx_tenant_id()));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS aislamiento_tenant ON public."ReclamacionesBuzonIntegracion";
            ALTER TABLE ONLY public."ReclamacionesBuzonIntegracion" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE public."ReclamacionesBuzonIntegracion" DISABLE ROW LEVEL SECURITY;
            """);
    }
}
