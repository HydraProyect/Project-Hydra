using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ResultadoEjecucionPurgaEIncidencias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CandidatosEnEjecucion",
                table: "SolicitudesPurga",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FallidosEnEjecucion",
                table: "SolicitudesPurga",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultadoEjecucion",
                table: "SolicitudesPurga",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SuprimidosEnEjecucion",
                table: "SolicitudesPurga",
                type: "integer",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SolicitudesPurga_TenantId_Id",
                table: "SolicitudesPurga",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "IncidenciasPurga",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SolicitudPurgaId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjetivoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Detalle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DetectadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidenciasPurga", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidenciasPurga_SolicitudesPurga_TenantId_SolicitudPurgaId",
                        columns: x => new { x.TenantId, x.SolicitudPurgaId },
                        principalTable: "SolicitudesPurga",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IncidenciasPurga_TenantId_SolicitudPurgaId",
                table: "IncidenciasPurga",
                columns: new[] { "TenantId", "SolicitudPurgaId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncidenciasPurga");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SolicitudesPurga_TenantId_Id",
                table: "SolicitudesPurga");

            migrationBuilder.DropColumn(
                name: "CandidatosEnEjecucion",
                table: "SolicitudesPurga");

            migrationBuilder.DropColumn(
                name: "FallidosEnEjecucion",
                table: "SolicitudesPurga");

            migrationBuilder.DropColumn(
                name: "ResultadoEjecucion",
                table: "SolicitudesPurga");

            migrationBuilder.DropColumn(
                name: "SuprimidosEnEjecucion",
                table: "SolicitudesPurga");
        }
    }
}
