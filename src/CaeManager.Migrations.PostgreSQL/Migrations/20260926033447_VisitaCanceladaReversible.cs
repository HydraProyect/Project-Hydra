using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class VisitaCanceladaReversible : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Visitas_ExpedientePendiente",
                table: "Visitas");

            migrationBuilder.AddColumn<DateTime>(
                name: "CanceladaEnUtc",
                table: "Visitas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EstaCancelada",
                table: "Visitas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MotivoCancelacion",
                table: "Visitas",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MotivoReactivacion",
                table: "Visitas",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReactivadaEnUtc",
                table: "Visitas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Visitas_ExpedientePendiente",
                table: "Visitas",
                columns: new[] { "TenantId", "FechaFin" },
                filter: "\"FechaHoraSolicitudUtc\" IS NOT NULL AND \"FechaHoraExpedienteCompletoUtc\" IS NULL AND NOT \"EstaEliminado\" AND NOT \"EstaCancelada\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Visitas_ExpedientePendiente",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "CanceladaEnUtc",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "EstaCancelada",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "MotivoCancelacion",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "MotivoReactivacion",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "ReactivadaEnUtc",
                table: "Visitas");

            migrationBuilder.CreateIndex(
                name: "IX_Visitas_ExpedientePendiente",
                table: "Visitas",
                columns: new[] { "TenantId", "FechaFin" },
                filter: "\"FechaHoraSolicitudUtc\" IS NOT NULL AND \"FechaHoraExpedienteCompletoUtc\" IS NULL AND NOT \"EstaEliminado\"");
        }
    }
}
