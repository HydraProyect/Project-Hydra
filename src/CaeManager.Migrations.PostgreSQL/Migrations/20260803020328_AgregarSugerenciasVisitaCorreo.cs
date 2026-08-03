using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarSugerenciasVisitaCorreo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SugerenciasVisitaCorreo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MensajeCorreoId = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: true),
                    FechaInicioSugerida = table.Column<DateOnly>(type: "date", nullable: true),
                    FechaFinSugerida = table.Column<DateOnly>(type: "date", nullable: true),
                    Resumen = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Resuelta = table.Column<bool>(type: "boolean", nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SugerenciasVisitaCorreo", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SugerenciasVisitaCorreo_MensajeCorreoId",
                table: "SugerenciasVisitaCorreo",
                column: "MensajeCorreoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SugerenciasVisitaCorreo");
        }
    }
}
