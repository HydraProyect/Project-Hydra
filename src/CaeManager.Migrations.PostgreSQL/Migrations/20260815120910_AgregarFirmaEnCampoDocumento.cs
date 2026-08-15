using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarFirmaEnCampoDocumento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FirmasEnCampoDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirmanteUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirmanteNombre = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FirmanteRol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FirmadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Ubicacion = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    HashSha256Pdf = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FirmasEnCampoDocumento", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FirmasEnCampoDocumento_Documentos_DocumentoId",
                        column: x => x.DocumentoId,
                        principalTable: "Documentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FirmasEnCampoDocumento_DocumentoId",
                table: "FirmasEnCampoDocumento",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_FirmasEnCampoDocumento_TenantId_DocumentoId",
                table: "FirmasEnCampoDocumento",
                columns: new[] { "TenantId", "DocumentoId" });

            // RLS en la misma tanda que crea la tabla — mismo criterio que
            // AgregarValidacionDocumentoOficial: el filtro global de EF ya la
            // protege; esto es la segunda línea (RLS sobre cae_app_runtime,
            // ver RUNBOOK-RLS.md).
            foreach (var tabla in TablasConRls)
            {
                migrationBuilder.Sql($@"
ALTER TABLE ""{tabla}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{tabla}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{tabla}"";
CREATE POLICY aislamiento_tenant ON ""{tabla}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
            }
        }

        private static readonly string[] TablasConRls =
        [
            "FirmasEnCampoDocumento",
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FirmasEnCampoDocumento");
        }
    }
}
