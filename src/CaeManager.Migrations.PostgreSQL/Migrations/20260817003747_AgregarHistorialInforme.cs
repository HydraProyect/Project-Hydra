using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarHistorialInforme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HistorialInformes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoInforme = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClienteNombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GeneradoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GeneradoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistorialInformes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistorialInformes_TenantId_GeneradoEnUtc",
                table: "HistorialInformes",
                columns: new[] { "TenantId", "GeneradoEnUtc" });

            // RLS en la misma migración que crea la tabla, desde el
            // principio (ver RUNBOOK-RLS.md y la lección de
            // HabilitarRlsEstadosAutomatizacion, que tuvo que corregirse a
            // posteriori).
            migrationBuilder.Sql(@"
ALTER TABLE ""HistorialInformes"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""HistorialInformes"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""HistorialInformes"";
CREATE POLICY aislamiento_tenant ON ""HistorialInformes""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""HistorialInformes"";
");

            migrationBuilder.DropTable(
                name: "HistorialInformes");
        }
    }
}
