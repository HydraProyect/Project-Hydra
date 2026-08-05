using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarUmbralesVisitaAParametroSistema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HorasAvisoVisita",
                table: "ParametrosSistema",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HorasCriticasVisita",
                table: "ParametrosSistema",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "ParametrosSistema",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                columns: new[] { "HorasAvisoVisita", "HorasCriticasVisita" },
                values: new object[] { 48, 24 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HorasAvisoVisita",
                table: "ParametrosSistema");

            migrationBuilder.DropColumn(
                name: "HorasCriticasVisita",
                table: "ParametrosSistema");
        }
    }
}
