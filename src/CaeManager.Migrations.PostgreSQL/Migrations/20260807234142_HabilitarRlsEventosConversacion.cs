using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Mismo criterio que HabilitarRlsSugerenciasVisitaCorreo (P1-3): el RLS
    /// se añade en la misma tanda que crea la tabla, no en otra ronda de
    /// auditoría — ver RUNBOOK-RLS.md.
    /// </summary>
    public partial class HabilitarRlsEventosConversacion : Migration
    {
        private const string Tabla = "EventosConversacion";

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
