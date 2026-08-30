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

            // Sin CreateIndex(IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes)
            // aquí — incidente de despliegue en staging, 2026-08-30: esta
            // migración (PR #366) se mergeó a main ANTES que
            // ReemplazarProcesadoPorEstadoEnEventoWebhook (PR #363), así que
            // en un entorno desplegado de forma incremental corrió PRIMERO,
            // con un CreateIndex filtrado sobre "Procesado" (la columna
            // todavía existía en ese momento). Cuando la migración de #363
            // llegó más tarde y eliminó "Procesado", PostgreSQL arrastró
            // consigo ese índice parcial (una columna no puede desaparecer
            // dejando un índice que la filtra) — y como esta migración ya
            // estaba registrada en __EFMigrationsHistory, nunca volvió a
            // correr para recrearlo. Por eso ReemplazarProcesadoPorEstadoEnEventoWebhook
            // es ahora quien crea este índice, con CREATE INDEX IF NOT
            // EXISTS: así queda bien tanto en un despliegue incremental como
            // staging (donde esta migración ya corrió y el índice desapareció
            // con la columna) como en una base fresca (donde, por nombre de
            // fichero, ReemplazarProcesadoPorEstadoEnEventoWebhook se aplica
            // ANTES que esta, y crearía el índice sin que esta migración
            // tuviera nada que repetir).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ParametrosSistema_TenantId",
                table: "ParametrosSistema");

            // Sin DropIndex(IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes)
            // aquí: esta migración ya no lo crea (ver Up) — quien lo crea es
            // ReemplazarProcesadoPorEstadoEnEventoWebhook, y es su propio
            // Down quien debe deshacerlo.
        }
    }
}
