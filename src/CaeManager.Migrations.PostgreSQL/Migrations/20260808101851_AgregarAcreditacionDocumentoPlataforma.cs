using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarAcreditacionDocumentoPlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_CanalesGestionDocumental_TenantId_Id",
                table: "CanalesGestionDocumental",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "AcreditacionesDocumentoPlataforma",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    CanalGestionDocumentalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcreditacionesDocumentoPlataforma", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AcreditacionesDocumentoPlataforma_CanalesGestionDocumental_~",
                        columns: x => new { x.TenantId, x.CanalGestionDocumentalId },
                        principalTable: "CanalesGestionDocumental",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AcreditacionesDocumentoPlataforma_Documentos_TenantId_Docum~",
                        columns: x => new { x.TenantId, x.DocumentoId },
                        principalTable: "Documentos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RechazosAcreditacionDocumentoPlataforma",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AcreditacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Causa = table.Column<int>(type: "integer", nullable: false),
                    MotivoLiteral = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RechazosAcreditacionDocumentoPlataforma", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RechazosAcreditacionDocumentoPlataforma_AcreditacionesDocum~",
                        column: x => x.AcreditacionId,
                        principalTable: "AcreditacionesDocumentoPlataforma",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AcreditacionesDocumentoPlataforma_TenantId_CanalGestionDocu~",
                table: "AcreditacionesDocumentoPlataforma",
                columns: new[] { "TenantId", "CanalGestionDocumentalId" });

            migrationBuilder.CreateIndex(
                name: "IX_AcreditacionesDocumentoPlataforma_TenantId_DocumentoId",
                table: "AcreditacionesDocumentoPlataforma",
                columns: new[] { "TenantId", "DocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_AcreditacionesDocumentoPlataforma_TenantId_DocumentoId_Cana~",
                table: "AcreditacionesDocumentoPlataforma",
                columns: new[] { "TenantId", "DocumentoId", "CanalGestionDocumentalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RechazosAcreditacionDocumentoPlataforma_AcreditacionId",
                table: "RechazosAcreditacionDocumentoPlataforma",
                column: "AcreditacionId");

            migrationBuilder.CreateIndex(
                name: "IX_RechazosAcreditacionDocumentoPlataforma_TenantId_Acreditaci~",
                table: "RechazosAcreditacionDocumentoPlataforma",
                columns: new[] { "TenantId", "AcreditacionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RechazosAcreditacionDocumentoPlataforma");

            migrationBuilder.DropTable(
                name: "AcreditacionesDocumentoPlataforma");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CanalesGestionDocumental_TenantId_Id",
                table: "CanalesGestionDocumental");
        }
    }
}
