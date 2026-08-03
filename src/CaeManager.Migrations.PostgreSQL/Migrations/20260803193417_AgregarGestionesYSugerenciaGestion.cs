using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarGestionesYSugerenciaGestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Gestiones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    Notas = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MensajeCorreoOrigenId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Gestiones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Gestiones_Centros_TenantId_CentroId",
                        columns: x => new { x.TenantId, x.CentroId },
                        principalTable: "Centros",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Gestiones_TiposDocumento_TenantId_TipoDocumentoId",
                        columns: x => new { x.TenantId, x.TipoDocumentoId },
                        principalTable: "TiposDocumento",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Gestiones_Trabajadores_TenantId_TrabajadorId",
                        columns: x => new { x.TenantId, x.TrabajadorId },
                        principalTable: "Trabajadores",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SugerenciasGestionCorreo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MensajeCorreoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Resumen = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Resuelta = table.Column<bool>(type: "boolean", nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SugerenciasGestionCorreo", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Gestiones_CentroId",
                table: "Gestiones",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_Gestiones_TenantId_CentroId",
                table: "Gestiones",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_Gestiones_TenantId_TipoDocumentoId",
                table: "Gestiones",
                columns: new[] { "TenantId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Gestiones_TenantId_TrabajadorId",
                table: "Gestiones",
                columns: new[] { "TenantId", "TrabajadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Gestiones_TrabajadorId",
                table: "Gestiones",
                column: "TrabajadorId");

            migrationBuilder.CreateIndex(
                name: "IX_SugerenciasGestionCorreo_MensajeCorreoId",
                table: "SugerenciasGestionCorreo",
                column: "MensajeCorreoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Gestiones");

            migrationBuilder.DropTable(
                name: "SugerenciasGestionCorreo");
        }
    }
}
