using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Mismo criterio que HabilitarRlsSugerenciasVisitaCorreo (P1-3): el RLS
    /// se añade en la misma tanda que crea la tabla, no en otra ronda de
    /// auditoría. El filtro global de EF ya la protegía; esto es solo la
    /// segunda línea (RLS sobre <c>cae_app_runtime</c>, ver RUNBOOK-RLS.md),
    /// inofensiva hoy porque ese rol restringido no está activado en ningún
    /// entorno.
    /// </summary>
    public partial class HabilitarRlsGestiones : Migration
    {
        private const string TablaGestiones = "Gestiones";
        private const string TablaSugerencias = "SugerenciasGestionCorreo";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
ALTER TABLE ""{TablaGestiones}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaGestiones}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaGestiones}"";
CREATE POLICY aislamiento_tenant ON ""{TablaGestiones}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");

            migrationBuilder.Sql($@"
ALTER TABLE ""{TablaSugerencias}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaSugerencias}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaSugerencias}"";
CREATE POLICY aislamiento_tenant ON ""{TablaSugerencias}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaSugerencias}"";
ALTER TABLE ""{TablaSugerencias}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaSugerencias}"" DISABLE ROW LEVEL SECURITY;
");

            migrationBuilder.Sql($@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaGestiones}"";
ALTER TABLE ""{TablaGestiones}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaGestiones}"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
