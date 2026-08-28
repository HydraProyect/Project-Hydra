using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarTipoDocumentoAlias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TiposDocumentoAlias",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Texto = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TiposDocumentoAlias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TiposDocumentoAlias_TiposDocumento_TipoDocumentoId",
                        column: x => x.TipoDocumentoId,
                        principalTable: "TiposDocumento",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumentoAlias_TenantId_TipoDocumentoId_Texto",
                table: "TiposDocumentoAlias",
                columns: new[] { "TenantId", "TipoDocumentoId", "Texto" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumentoAlias_TipoDocumentoId",
                table: "TiposDocumentoAlias",
                column: "TipoDocumentoId");

            // RLS en la misma migración que crea la tabla, desde el
            // principio (ver RUNBOOK-RLS.md y la lección de
            // HabilitarRlsEstadosAutomatizacion, que tuvo que corregirse a
            // posteriori).
            migrationBuilder.Sql(@"
ALTER TABLE ""TiposDocumentoAlias"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""TiposDocumentoAlias"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""TiposDocumentoAlias"";
CREATE POLICY aislamiento_tenant ON ""TiposDocumentoAlias""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""TiposDocumentoAlias"";
");

            migrationBuilder.DropTable(
                name: "TiposDocumentoAlias");
        }
    }
}
