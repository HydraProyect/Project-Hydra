using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Segunda línea de aislamiento para <c>RegistrosTiempoGestion</c> (tabla nueva
    /// con <c>TenantId</c>, ver RUNBOOK-RLS.md). Misma política exacta que las 46
    /// tablas anteriores — no reinventa el criterio, solo lo extiende. Sin esta
    /// migración, <c>PoliticasRlsCubrenModeloTests</c> falla en CI, que es
    /// precisamente para lo que existe ese test.
    /// </summary>
    public partial class HabilitarRlsRegistroTiempoGestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""RegistrosTiempoGestion"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""RegistrosTiempoGestion"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""RegistrosTiempoGestion"";
CREATE POLICY aislamiento_tenant ON ""RegistrosTiempoGestion""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""RegistrosTiempoGestion"";
ALTER TABLE ""RegistrosTiempoGestion"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""RegistrosTiempoGestion"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
