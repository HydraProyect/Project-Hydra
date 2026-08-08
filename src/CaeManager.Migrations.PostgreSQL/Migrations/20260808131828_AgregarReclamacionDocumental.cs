using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarReclamacionDocumental : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReclamacionesDocumentales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnviadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinatarioEmail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FechaEnvioUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReclamacionesDocumentales", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReclamacionesDocumentalesDocumentos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReclamacionDocumentalId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReclamacionesDocumentalesDocumentos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReclamacionesDocumentalesDocumentos_ReclamacionesDocumental~",
                        column: x => x.ReclamacionDocumentalId,
                        principalTable: "ReclamacionesDocumentales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReclamacionesDocumentales_TenantId_ClienteId",
                table: "ReclamacionesDocumentales",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReclamacionesDocumentalesDocumentos_ReclamacionDocumentalId",
                table: "ReclamacionesDocumentalesDocumentos",
                column: "ReclamacionDocumentalId");

            migrationBuilder.CreateIndex(
                name: "IX_ReclamacionesDocumentalesDocumentos_TenantId_DocumentoId",
                table: "ReclamacionesDocumentalesDocumentos",
                columns: new[] { "TenantId", "DocumentoId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReclamacionesDocumentalesDocumentos");

            migrationBuilder.DropTable(
                name: "ReclamacionesDocumentales");
        }
    }
}
