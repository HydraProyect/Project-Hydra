using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarGestorPropietarioIdAConexionIntegracion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GestorPropietarioId",
                table: "ConexionesIntegracion",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConexionesIntegracion_TenantId_GestorPropietarioId",
                table: "ConexionesIntegracion",
                columns: new[] { "TenantId", "GestorPropietarioId" },
                unique: true,
                filter: "\"GestorPropietarioId\" IS NOT NULL AND NOT \"EstaEliminado\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConexionesIntegracion_TenantId_GestorPropietarioId",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "GestorPropietarioId",
                table: "ConexionesIntegracion");
        }
    }
}
