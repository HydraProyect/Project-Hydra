using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AcotarHiloExternoIdPorConexion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversaciones_TenantId_HiloExternoId",
                table: "Conversaciones");

            migrationBuilder.CreateIndex(
                name: "IX_Conversaciones_TenantId_ConexionIntegracionId_HiloExternoId",
                table: "Conversaciones",
                columns: new[] { "TenantId", "ConexionIntegracionId", "HiloExternoId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversaciones_TenantId_ConexionIntegracionId_HiloExternoId",
                table: "Conversaciones");

            migrationBuilder.CreateIndex(
                name: "IX_Conversaciones_TenantId_HiloExternoId",
                table: "Conversaciones",
                columns: new[] { "TenantId", "HiloExternoId" },
                unique: true);
        }
    }
}
