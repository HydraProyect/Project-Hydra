using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ArreglarIndiceAsignacionesActivas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Asignaciones_TenantId_TrabajadorId_CentroId_FechaAlta",
                table: "Asignaciones");

            migrationBuilder.CreateIndex(
                name: "IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa",
                table: "Asignaciones",
                columns: new[] { "TenantId", "TrabajadorId", "CentroId" },
                unique: true,
                filter: "\"FechaBaja\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa",
                table: "Asignaciones");

            migrationBuilder.CreateIndex(
                name: "IX_Asignaciones_TenantId_TrabajadorId_CentroId_FechaAlta",
                table: "Asignaciones",
                columns: new[] { "TenantId", "TrabajadorId", "CentroId", "FechaAlta" },
                unique: true);
        }
    }
}
