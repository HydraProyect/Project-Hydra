using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgregarSolicitudesPurga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolicitudesPurga",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TipoDato = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RegistrosAfectados = table.Column<int>(type: "INTEGER", nullable: false),
                    FechaCorte = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Estado = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    DetectadaEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AvisadaAlTenantEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FechaEjecucionProgramada = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AutorizadaPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EjecutadaEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Motivo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesPurga", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesPurga_TipoDato_Estado",
                table: "SolicitudesPurga",
                columns: new[] { "TipoDato", "Estado" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolicitudesPurga");
        }
    }
}
