using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// RLS para VerificacionesExternaSubcontrata, en la misma tanda que la
    /// crea (AgregarNivelServicioYVerificacionExternaSubcontrata) — mismo
    /// criterio que HabilitarRlsClasificacionRelevanciaCae: no repetir el
    /// hallazgo P1-3. El filtro global de EF ya la protege; esto es la
    /// segunda línea (RLS sobre cae_app_runtime, ver RUNBOOK-RLS.md).
    /// </summary>
    public partial class HabilitarRlsVerificacionesExternaSubcontrata : Migration
    {
        private const string Tabla = "VerificacionesExternaSubcontrata";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
ALTER TABLE ""{Tabla}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{Tabla}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{Tabla}"";
CREATE POLICY aislamiento_tenant ON ""{Tabla}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""{Tabla}"";
ALTER TABLE ""{Tabla}"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""{Tabla}"" DISABLE ROW LEVEL SECURITY;
");
        }
    }
}
