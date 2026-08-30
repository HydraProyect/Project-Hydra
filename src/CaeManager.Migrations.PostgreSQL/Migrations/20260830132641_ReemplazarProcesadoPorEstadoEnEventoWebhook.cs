using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ReemplazarProcesadoPorEstadoEnEventoWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventosWebhook_TenantId_Procesado",
                table: "EventosWebhook");

            // Nullable primero para poder rellenarla desde "Procesado" antes
            // de exigir NOT NULL — evita perder, para las filas existentes,
            // la distinción entre "terminó bien" y "se dio por perdido tras
            // agotar los intentos" que ya describía "Intentos" pero que el
            // booleano antiguo no reflejaba.
            migrationBuilder.AddColumn<string>(
                name: "Estado",
                table: "EventosWebhook",
                type: "text",
                nullable: true);

            // MaximoIntentos (5) en literal, no como referencia al dominio:
            // una migración fija en el tiempo el contrato tal como era al
            // escribirla, no lo que el dominio diga hoy.
            migrationBuilder.Sql("""
                UPDATE "EventosWebhook"
                SET "Estado" = CASE
                    WHEN NOT "Procesado" THEN 'Pendiente'
                    WHEN "Procesado" AND "Intentos" >= 5 THEN 'DescartadoDefinitivo'
                    ELSE 'Completado'
                END;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Estado",
                table: "EventosWebhook",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "Procesado",
                table: "EventosWebhook");

            migrationBuilder.AddColumn<DateTime>(
                name: "IniciadoEnUtc",
                table: "EventosWebhook",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SiguienteIntentoEnUtc",
                table: "EventosWebhook",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventosWebhook_TenantId_Estado",
                table: "EventosWebhook",
                columns: new[] { "TenantId", "Estado" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventosWebhook_TenantId_Estado",
                table: "EventosWebhook");

            migrationBuilder.DropColumn(
                name: "Estado",
                table: "EventosWebhook");

            migrationBuilder.DropColumn(
                name: "IniciadoEnUtc",
                table: "EventosWebhook");

            migrationBuilder.DropColumn(
                name: "SiguienteIntentoEnUtc",
                table: "EventosWebhook");

            migrationBuilder.AddColumn<bool>(
                name: "Procesado",
                table: "EventosWebhook",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_EventosWebhook_TenantId_Procesado",
                table: "EventosWebhook",
                columns: new[] { "TenantId", "Procesado" });
        }
    }
}
