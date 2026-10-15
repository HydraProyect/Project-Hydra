using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeFichaAltasPlataformaTrabajador : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "CaducidadPermisoTrabajo",
                table: "Trabajadores",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FechaAltaEmpresa",
                table: "Trabajadores",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FechaBajaEmpresa",
                table: "Trabajadores",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiguraEnRnt",
                table: "Trabajadores",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MotivoPermisoTrabajo",
                table: "Trabajadores",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Nacionalidad",
                table: "Trabajadores",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NumeroAfiliacionSeguridadSocial",
                table: "Trabajadores",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TipoPermisoTrabajo",
                table: "Trabajadores",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TipoVinculo",
                table: "Trabajadores",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CaducidadPermisoTrabajo",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "FechaAltaEmpresa",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "FechaBajaEmpresa",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "FiguraEnRnt",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "MotivoPermisoTrabajo",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "Nacionalidad",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "NumeroAfiliacionSeguridadSocial",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "TipoPermisoTrabajo",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "TipoVinculo",
                table: "Trabajadores");
        }
    }
}
