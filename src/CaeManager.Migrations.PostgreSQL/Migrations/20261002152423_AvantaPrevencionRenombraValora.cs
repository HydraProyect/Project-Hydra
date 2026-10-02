using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AvantaPrevencionRenombraValora : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "DominiosProveedorPlataformaCae",
                columns: new[] { "Id", "Dominio", "ProveedorPlataformaCaeId" },
                values: new object[] { new Guid("7000000b-0001-0000-0000-000000000001"), "avantaprevencion.com", new Guid("6000000b-0000-0000-0000-000000000001") });

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000b-0000-0000-0000-000000000001"),
                column: "Nombre",
                value: "Avanta Prevención");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "DominiosProveedorPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("7000000b-0001-0000-0000-000000000001"));

            migrationBuilder.UpdateData(
                table: "ProveedoresPlataformaCae",
                keyColumn: "Id",
                keyValue: new Guid("6000000b-0000-0000-0000-000000000001"),
                column: "Nombre",
                value: "Valora");
        }
    }
}
