using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarHistorialImportacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HistorialImportaciones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Plantilla = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NombreArchivo = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    EjecutadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EjecutadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Exitosa = table.Column<bool>(type: "boolean", nullable: false),
                    TotalCreados = table.Column<int>(type: "integer", nullable: false),
                    TotalAdvertencias = table.Column<int>(type: "integer", nullable: false),
                    TotalOmitidos = table.Column<int>(type: "integer", nullable: false),
                    MensajeError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistorialImportaciones", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistorialImportaciones_TenantId_EjecutadaEnUtc",
                table: "HistorialImportaciones",
                columns: new[] { "TenantId", "EjecutadaEnUtc" });

            // RLS en la misma migración que crea la tabla (ver RUNBOOK-RLS.md) —
            // HabilitarRlsEstadosAutomatizacion tuvo que corregirse a posteriori
            // porque PoliticasRlsCubrenModeloTests lo detectó en rojo; esta vez
            // se incluye desde el principio.
            migrationBuilder.Sql(@"
ALTER TABLE ""HistorialImportaciones"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""HistorialImportaciones"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""HistorialImportaciones"";
CREATE POLICY aislamiento_tenant ON ""HistorialImportaciones""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""HistorialImportaciones"";
");

            migrationBuilder.DropTable(
                name: "HistorialImportaciones");
        }
    }
}
