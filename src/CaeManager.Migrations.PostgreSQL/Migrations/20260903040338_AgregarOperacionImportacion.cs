using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarOperacionImportacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperacionesImportacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfirmadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperacionesImportacion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperacionesImportacion_TenantId_OperacionId",
                table: "OperacionesImportacion",
                columns: new[] { "TenantId", "OperacionId" },
                unique: true);

            // RLS en la misma migración que crea la tabla: es EntidadConTenant, y
            // los ratchets CoberturaRlsDelModeloTests/PoliticasRlsCubrenModeloTests
            // exigen política para toda tabla con TenantId. El filtro global de EF
            // ya la protege; esto es la segunda línea, sobre cae_app_runtime
            // (RUNBOOK-RLS.md).
            migrationBuilder.Sql(@"
ALTER TABLE ""OperacionesImportacion"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""OperacionesImportacion"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""OperacionesImportacion"";
CREATE POLICY aislamiento_tenant ON ""OperacionesImportacion""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperacionesImportacion");
        }
    }
}
