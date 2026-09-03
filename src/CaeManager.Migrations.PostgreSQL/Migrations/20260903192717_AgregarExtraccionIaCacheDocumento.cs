using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarExtraccionIaCacheDocumento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_ExtraccionesIaCache_TenantId_Id",
                table: "ExtraccionesIaCache",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "ExtraccionesIaCacheDocumentos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExtraccionIaCacheId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtraccionesIaCacheDocumentos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExtraccionesIaCacheDocumentos_Documentos_TenantId_Documento~",
                        columns: x => new { x.TenantId, x.DocumentoId },
                        principalTable: "Documentos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExtraccionesIaCacheDocumentos_ExtraccionesIaCache_TenantId_~",
                        columns: x => new { x.TenantId, x.ExtraccionIaCacheId },
                        principalTable: "ExtraccionesIaCache",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtraccionesIaCache_TenantId_Id",
                table: "ExtraccionesIaCache",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtraccionesIaCacheDocumentos_TenantId_DocumentoId",
                table: "ExtraccionesIaCacheDocumentos",
                columns: new[] { "TenantId", "DocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExtraccionesIaCacheDocumentos_TenantId_ExtraccionIaCacheId_~",
                table: "ExtraccionesIaCacheDocumentos",
                columns: new[] { "TenantId", "ExtraccionIaCacheId", "DocumentoId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExtraccionesIaCacheDocumentos");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ExtraccionesIaCache_TenantId_Id",
                table: "ExtraccionesIaCache");

            migrationBuilder.DropIndex(
                name: "IX_ExtraccionesIaCache_TenantId_Id",
                table: "ExtraccionesIaCache");
        }
    }
}
