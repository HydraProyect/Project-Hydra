using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class IndicesParametroSistemaYEventoWebhookPendientes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventosWebhook_TenantId_Procesado",
                table: "EventosWebhook");

            migrationBuilder.CreateIndex(
                name: "IX_ParametrosSistema_TenantId",
                table: "ParametrosSistema",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes",
                table: "EventosWebhook",
                columns: new[] { "TenantId", "FechaRecepcionUtc" },
                filter: "NOT \"Procesado\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ParametrosSistema_TenantId",
                table: "ParametrosSistema");

            migrationBuilder.DropIndex(
                name: "IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes",
                table: "EventosWebhook");

            migrationBuilder.CreateIndex(
                name: "IX_EventosWebhook_TenantId_Procesado",
                table: "EventosWebhook",
                columns: new[] { "TenantId", "Procesado" });
        }
    }
}
