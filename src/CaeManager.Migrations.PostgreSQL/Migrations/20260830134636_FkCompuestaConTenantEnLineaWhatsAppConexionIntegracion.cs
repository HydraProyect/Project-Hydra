using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class FkCompuestaConTenantEnLineaWhatsAppConexionIntegracion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LineasWhatsApp_ConexionesIntegracion_ConexionIntegracionId",
                table: "LineasWhatsApp");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ConexionesIntegracion_TenantId_Id",
                table: "ConexionesIntegracion",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LineasWhatsApp_TenantId_ConexionIntegracionId",
                table: "LineasWhatsApp",
                columns: new[] { "TenantId", "ConexionIntegracionId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LineasWhatsApp_ConexionesIntegracion_TenantId_ConexionInteg~",
                table: "LineasWhatsApp",
                columns: new[] { "TenantId", "ConexionIntegracionId" },
                principalTable: "ConexionesIntegracion",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LineasWhatsApp_ConexionesIntegracion_TenantId_ConexionInteg~",
                table: "LineasWhatsApp");

            migrationBuilder.DropIndex(
                name: "IX_LineasWhatsApp_TenantId_ConexionIntegracionId",
                table: "LineasWhatsApp");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ConexionesIntegracion_TenantId_Id",
                table: "ConexionesIntegracion");

            migrationBuilder.AddForeignKey(
                name: "FK_LineasWhatsApp_ConexionesIntegracion_ConexionIntegracionId",
                table: "LineasWhatsApp",
                column: "ConexionIntegracionId",
                principalTable: "ConexionesIntegracion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
