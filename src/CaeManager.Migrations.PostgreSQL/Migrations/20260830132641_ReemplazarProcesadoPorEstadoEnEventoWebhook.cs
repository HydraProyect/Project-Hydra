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
            // IF EXISTS (no migrationBuilder.DropIndex, que genera un DROP
            // INDEX sin condicional) — incidente de staging, 2026-08-30: el
            // despliegue llevaba fallando desde el primer intento con
            // 'index "IX_EventosWebhook_TenantId_Procesado" does not exist',
            // repitiéndose en CADA redeploy posterior (PostgreSQL hace DDL
            // transaccional: la migración entera se revierte, así que
            // __EFMigrationsHistory nunca la registra como aplicada). El
            // índice sí existe en la cadena de migraciones tal como está
            // escrita (CrearIntegracionesMicrosoft365 lo crea con este mismo
            // nombre, y ninguna migración posterior lo toca) — el esquema
            // real de staging había divergido de esa cadena por una vía no
            // determinada. Sea cual sea la causa de esa divergencia, el DROP
            // no debería depender de que el estado físico coincida exactamente
            // con lo que la cadena de migraciones asume.
            migrationBuilder.Sql(
                """DROP INDEX IF EXISTS "IX_EventosWebhook_TenantId_Procesado";""");

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

            // Sin CreateIndex(TenantId, Estado) aquí a propósito: el índice
            // parcial (TenantId, FechaRecepcionUtc) filtrado sobre pendientes
            // ya cubre el caso de uso real — ver el siguiente bloque.

            // Esta migración es quien crea el índice parcial que, por
            // diseño, "pertenece" a IndicesParametroSistemaYEventoWebhookPendientes
            // (Módulo 8) — no esa migración. Motivo (mismo incidente de
            // despliegue, 2026-08-30): esa migración se mergeó a main ANTES
            // que esta, así que en un despliegue incremental como staging
            // corrió PRIMERO, con un CreateIndex filtrado sobre "Procesado"
            // (todavía existía en ese momento). Al llegar aquí y eliminar la
            // columna, PostgreSQL arrastra consigo cualquier índice que la
            // filtre — así que en staging, en este punto, el índice YA NO
            // EXISTE, y nadie vuelve a crearlo (esa migración ya está
            // registrada en __EFMigrationsHistory, no se re-ejecuta).
            // IF NOT EXISTS cubre el otro caso — una base fresca, donde por
            // nombre de fichero esta migración se aplica ANTES que la de
            // Módulo 8, y el índice todavía no existe cuando llega aquí.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes"
                ON "EventosWebhook" ("TenantId", "FechaRecepcionUtc")
                WHERE "Estado" = 'Pendiente';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """DROP INDEX IF EXISTS "IX_EventosWebhook_TenantId_FechaRecepcionUtc_Pendientes";""");

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
