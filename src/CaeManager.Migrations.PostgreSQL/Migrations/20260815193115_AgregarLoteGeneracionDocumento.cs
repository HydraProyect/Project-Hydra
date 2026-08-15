using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarLoteGeneracionDocumento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LotesGeneracionDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantillaDocumentoVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContextoJson = table.Column<string>(type: "text", nullable: true),
                    Estado = table.Column<string>(type: "text", nullable: false),
                    TotalItems = table.Column<int>(type: "integer", nullable: false),
                    ItemsCompletados = table.Column<int>(type: "integer", nullable: false),
                    ItemsFallidos = table.Column<int>(type: "integer", nullable: false),
                    UsuarioSolicitanteId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LotesGeneracionDocumento", x => x.Id);
                    table.UniqueConstraint("AK_LotesGeneracionDocumento_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_LotesGeneracionDocumento_PlantillasDocumentoVersion_Plantil~",
                        column: x => x.PlantillaDocumentoVersionId,
                        principalTable: "PlantillasDocumentoVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemsGeneracionDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoteGeneracionDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoGeneradoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Estado = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemsGeneracionDocumento", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemsGeneracionDocumento_LotesGeneracionDocumento_TenantId_~",
                        columns: x => new { x.TenantId, x.LoteGeneracionDocumentoId },
                        principalTable: "LotesGeneracionDocumento",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemsGeneracionDocumento_TenantId_LoteGeneracionDocumentoId",
                table: "ItemsGeneracionDocumento",
                columns: new[] { "TenantId", "LoteGeneracionDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_LotesGeneracionDocumento_PlantillaDocumentoVersionId",
                table: "LotesGeneracionDocumento",
                column: "PlantillaDocumentoVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_LotesGeneracionDocumento_TenantId_Id",
                table: "LotesGeneracionDocumento",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LotesGeneracionDocumento_TenantId_PlantillaDocumentoVersion~",
                table: "LotesGeneracionDocumento",
                columns: new[] { "TenantId", "PlantillaDocumentoVersionId" });

            // RLS en la misma tanda que crea las tablas — mismo criterio que
            // AgregarPlantillasDocumento/AgregarTipoDocumentoYDocumentoGenerado.
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
            "LotesGeneracionDocumento",
            "ItemsGeneracionDocumento",
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemsGeneracionDocumento");

            migrationBuilder.DropTable(
                name: "LotesGeneracionDocumento");
        }
    }
}
