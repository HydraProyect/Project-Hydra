using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Mismo criterio que <c>HabilitarRlsEventosConversacion</c>: el RLS se
    /// añade en la misma tanda que crea la tabla, no en otra ronda de
    /// auditoría. El filtro global de EF ya la protegía; esto es solo la
    /// segunda línea (RLS sobre <c>cae_app_runtime</c>, ver RUNBOOK-RLS.md).
    /// </summary>
    public partial class HabilitarRlsAcreditacionDocumentoPlataforma : Migration
    {
        private const string TablaAcreditaciones = "AcreditacionesDocumentoPlataforma";
        private const string TablaRechazos = "RechazosAcreditacionDocumentoPlataforma";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
ALTER TABLE ""{TablaAcreditaciones}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaAcreditaciones}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaAcreditaciones}"";
CREATE POLICY aislamiento_tenant ON ""{TablaAcreditaciones}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE ""{TablaRechazos}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaRechazos}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaRechazos}"";
CREATE POLICY aislamiento_tenant ON ""{TablaRechazos}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaRechazos}"";
ALTER TABLE ""{TablaRechazos}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaRechazos}"" DISABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaAcreditaciones}"";
ALTER TABLE ""{TablaAcreditaciones}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaAcreditaciones}"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
