using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeSeguridadAccesoTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExigeSegundoFactorTodosLosPerfiles",
                table: "ParametrosSistema",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MinutosInactividadSesion",
                table: "ParametrosSistema",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.UpdateData(
                table: "ParametrosSistema",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                columns: new[] { "ExigeSegundoFactorTodosLosPerfiles", "MinutosInactividadSesion" },
                values: new object[] { false, 30 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExigeSegundoFactorTodosLosPerfiles",
                table: "ParametrosSistema");

            migrationBuilder.DropColumn(
                name: "MinutosInactividadSesion",
                table: "ParametrosSistema");
        }
    }
}
