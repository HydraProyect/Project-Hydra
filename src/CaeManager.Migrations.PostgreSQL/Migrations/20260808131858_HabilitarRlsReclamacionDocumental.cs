using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Mismo criterio que <c>HabilitarRlsAcreditacionDocumentoPlataforma</c>:
    /// el RLS se añade en la misma tanda que crea la tabla. El filtro global
    /// de EF ya la protegía; esto es la segunda línea (RLS sobre
    /// <c>cae_app_runtime</c>, ver RUNBOOK-RLS.md).
    /// </summary>
    public partial class HabilitarRlsReclamacionDocumental : Migration
    {
        private const string TablaReclamaciones = "ReclamacionesDocumentales";
        private const string TablaDocumentos = "ReclamacionesDocumentalesDocumentos";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
ALTER TABLE ""{TablaReclamaciones}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaReclamaciones}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaReclamaciones}"";
CREATE POLICY aislamiento_tenant ON ""{TablaReclamaciones}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE ""{TablaDocumentos}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaDocumentos}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaDocumentos}"";
CREATE POLICY aislamiento_tenant ON ""{TablaDocumentos}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaDocumentos}"";
ALTER TABLE ""{TablaDocumentos}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaDocumentos}"" DISABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS aislamiento_tenant ON ""{TablaReclamaciones}"";
ALTER TABLE ""{TablaReclamaciones}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{TablaReclamaciones}"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
