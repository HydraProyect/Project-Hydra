using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarPresupuestoMensualIaParametroSistema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PresupuestoMensualIaUsd",
                table: "ParametrosSistema",
                type: "numeric",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "ParametrosSistema",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                column: "PresupuestoMensualIaUsd",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PresupuestoMensualIaUsd",
                table: "ParametrosSistema");
        }
    }
}
