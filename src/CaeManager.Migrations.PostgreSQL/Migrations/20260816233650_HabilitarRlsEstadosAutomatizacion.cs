using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Segunda línea de aislamiento para <c>EstadosAutomatizacion</c> (tabla nueva
    /// con <c>TenantId</c>, ver RUNBOOK-RLS.md). Misma política exacta que las
    /// tablas anteriores — no reinventa el criterio, solo lo extiende. Sin esta
    /// migración, <c>PoliticasRlsCubrenModeloTests</c> falla en CI, que es
    /// precisamente para lo que existe ese test.
    /// </summary>
    public partial class HabilitarRlsEstadosAutomatizacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""EstadosAutomatizacion"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""EstadosAutomatizacion"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""EstadosAutomatizacion"";
CREATE POLICY aislamiento_tenant ON ""EstadosAutomatizacion""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""EstadosAutomatizacion"";
ALTER TABLE ""EstadosAutomatizacion"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""EstadosAutomatizacion"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
