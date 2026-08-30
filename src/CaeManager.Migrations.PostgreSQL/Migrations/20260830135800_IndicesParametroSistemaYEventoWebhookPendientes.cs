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
            // Sin DropIndex(IX_EventosWebhook_TenantId_Procesado) aquí: la
            // migración ReemplazarProcesadoPorEstadoEnEventoWebhook (auditoría
            // de colas, timestamp anterior, mergeada después por orden de PR)
            // ya la eliminó al retirar la columna "Procesado" — repetirla
            // fallaría contra un índice que ya no existe.
            migrationBuilder.CreateIndex(
                name: "IX_ParametrosSistema_TenantId",
                table: "ParametrosSistema",
                column: "TenantId",
                unique: true);

            // Filtro sobre "Estado" (no "Procesado"): para cuando esta
            // migración corre, ReemplazarProcesadoPorEstadoEnEventoWebhook ya
            // renombró la columna — ver EventoWebhookConfiguration.
            migrationBuilder.CreateIndex(
                name: "IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes",
                table: "EventosWebhook",
                columns: new[] { "TenantId", "FechaRecepcionUtc" },
                filter: "\"Estado\" = 'Pendiente'");
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

            // Sin recrear IX_EventosWebhook_TenantId_Procesado aquí: en un
            // rollback, esta Down corre ANTES que la de
            // ReemplazarProcesadoPorEstadoEnEventoWebhook (orden inverso), así
            // que la columna "Procesado" todavía no existe en este punto —
            // esa migración es quien la recrea al deshacerse ella misma.
        }
    }
}
