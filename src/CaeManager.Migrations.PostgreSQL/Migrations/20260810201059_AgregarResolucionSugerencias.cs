using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarResolucionSugerencias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Resolucion",
                table: "SugerenciasVisitaCorreo",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResueltaEnUtc",
                table: "SugerenciasVisitaCorreo",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResueltaPorUsuarioId",
                table: "SugerenciasVisitaCorreo",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Resolucion",
                table: "DetallesSugerenciaGestionCorreo",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResueltaEnUtc",
                table: "DetallesSugerenciaGestionCorreo",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResueltaPorUsuarioId",
                table: "DetallesSugerenciaGestionCorreo",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Resolucion",
                table: "SugerenciasVisitaCorreo");

            migrationBuilder.DropColumn(
                name: "ResueltaEnUtc",
                table: "SugerenciasVisitaCorreo");

            migrationBuilder.DropColumn(
                name: "ResueltaPorUsuarioId",
                table: "SugerenciasVisitaCorreo");

            migrationBuilder.DropColumn(
                name: "Resolucion",
                table: "DetallesSugerenciaGestionCorreo");

            migrationBuilder.DropColumn(
                name: "ResueltaEnUtc",
                table: "DetallesSugerenciaGestionCorreo");

            migrationBuilder.DropColumn(
                name: "ResueltaPorUsuarioId",
                table: "DetallesSugerenciaGestionCorreo");
        }
    }
}
