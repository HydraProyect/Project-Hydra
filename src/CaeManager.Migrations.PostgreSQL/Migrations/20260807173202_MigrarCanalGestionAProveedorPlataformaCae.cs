using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class MigrarCanalGestionAProveedorPlataformaCae : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Orden importa: el scaffold por defecto ponía el DropColumn de
            // NombrePlataforma primero, lo que habría perdido cualquier fila real
            // sin trasladar (PLAN-EJECUCION-UX.md § Parte 2 (a), Lote 2-B). La
            // columna nueva tiene que existir y poblarse ANTES de tirar la vieja.
            migrationBuilder.AddColumn<Guid>(
                name: "ProveedorPlataformaCaeId",
                table: "CanalesGestionDocumental",
                type: "uuid",
                nullable: true);

            // Matching sugerido por nombre exacto (recortado, sin distinguir
            // mayúsculas) contra el catálogo global — mismo criterio de "mejor
            // esfuerzo, sin inventar coincidencias" que ya usó RetirarRequisitoDocumental
            // para su propio backfill. Las filas que no matcheen quedan en NULL:
            // se resuelven automáticamente por URL (IResolucionProveedorPlataformaCaeService)
            // o a mano la próxima vez que alguien edite ese acceso — nunca se
            // adivina un proveedor por similitud parcial.
            migrationBuilder.Sql("""
                UPDATE "CanalesGestionDocumental" c
                SET "ProveedorPlataformaCaeId" = p."Id"
                FROM "ProveedoresPlataformaCae" p
                WHERE c."NombrePlataforma" IS NOT NULL
                  AND TRIM(LOWER(c."NombrePlataforma")) = TRIM(LOWER(p."Nombre"));
                """);

            migrationBuilder.DropColumn(
                name: "NombrePlataforma",
                table: "CanalesGestionDocumental");

            migrationBuilder.CreateIndex(
                name: "IX_CanalesGestionDocumental_ProveedorPlataformaCaeId",
                table: "CanalesGestionDocumental",
                column: "ProveedorPlataformaCaeId");

            migrationBuilder.AddForeignKey(
                name: "FK_CanalesGestionDocumental_ProveedoresPlataformaCae_Proveedor~",
                table: "CanalesGestionDocumental",
                column: "ProveedorPlataformaCaeId",
                principalTable: "ProveedoresPlataformaCae",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CanalesGestionDocumental_ProveedoresPlataformaCae_Proveedor~",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropIndex(
                name: "IX_CanalesGestionDocumental_ProveedorPlataformaCaeId",
                table: "CanalesGestionDocumental");

            migrationBuilder.AddColumn<string>(
                name: "NombrePlataforma",
                table: "CanalesGestionDocumental",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            // Reversa del backfill: recupera el nombre del proveedor resuelto.
            // Los que quedaron sin resolver (NULL) vuelven también a NULL — no
            // había texto original que recuperar para esos.
            migrationBuilder.Sql("""
                UPDATE "CanalesGestionDocumental" c
                SET "NombrePlataforma" = p."Nombre"
                FROM "ProveedoresPlataformaCae" p
                WHERE c."ProveedorPlataformaCaeId" = p."Id";
                """);

            migrationBuilder.DropColumn(
                name: "ProveedorPlataformaCaeId",
                table: "CanalesGestionDocumental");
        }
    }
}
