using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarAntelacionAVisita : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AntelacionEfectivaHoras",
                table: "Visitas",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "AntelacionNominalHoras",
                table: "Visitas",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Atribucion",
                table: "Visitas",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ConversacionOrigenId",
                table: "Visitas",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaHoraExpedienteCompletoUtc",
                table: "Visitas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaHoraSolicitudUtc",
                table: "Visitas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "HoraEstimadaAcceso",
                table: "Visitas",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Tramo",
                table: "Visitas",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Visitas_ExpedientePendiente",
                table: "Visitas",
                columns: new[] { "TenantId", "FechaFin" },
                filter: "\"FechaHoraSolicitudUtc\" IS NOT NULL AND \"FechaHoraExpedienteCompletoUtc\" IS NULL AND NOT \"EstaEliminado\"");

            migrationBuilder.CreateIndex(
                name: "IX_Visitas_TenantId_ConversacionOrigenId",
                table: "Visitas",
                columns: new[] { "TenantId", "ConversacionOrigenId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Visitas_ExpedientePendiente",
                table: "Visitas");

            migrationBuilder.DropIndex(
                name: "IX_Visitas_TenantId_ConversacionOrigenId",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "AntelacionEfectivaHoras",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "AntelacionNominalHoras",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "Atribucion",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "ConversacionOrigenId",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "FechaHoraExpedienteCompletoUtc",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "FechaHoraSolicitudUtc",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "HoraEstimadaAcceso",
                table: "Visitas");

            migrationBuilder.DropColumn(
                name: "Tramo",
                table: "Visitas");
        }
    }
}
