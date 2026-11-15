using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RecuperacionHuecoCorreoM365 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CubreDesdeUtc",
                table: "EventosWebhook",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecuperacionHuecoCubreDesdeUtc",
                table: "ConexionesIntegracion",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecuperacionHuecoCursorUtc",
                table: "ConexionesIntegracion",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RecuperacionHuecoListadoCompleto",
                table: "ConexionesIntegracion",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CubreDesdeUtc",
                table: "EventosWebhook");

            migrationBuilder.DropColumn(
                name: "RecuperacionHuecoCubreDesdeUtc",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "RecuperacionHuecoCursorUtc",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "RecuperacionHuecoListadoCompleto",
                table: "ConexionesIntegracion");
        }
    }
}
