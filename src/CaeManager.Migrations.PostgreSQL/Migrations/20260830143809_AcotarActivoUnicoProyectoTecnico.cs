using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AcotarActivoUnicoProyectoTecnico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Auditoría Módulo 5, hallazgo crítico 11/9: el índice anterior
            // incluía FechaAlta, así que la carrera SELECT-luego-INSERT de
            // AsignarTecnicoProyectoCommand podía dejar dos filas activas
            // para el mismo proyecto-trabajador con fechas de alta distintas.
            // Antes de acotar a "una activa" hay que cerrar cualquier
            // duplicado heredado, o la creación del índice fallaría contra
            // filas ya existentes. Se conserva la más reciente (FechaAlta,
            // desempate por Id) y se da de baja las demás en la fecha en que
            // se detecta la duplicidad — igual criterio que
            // AcotarResponsableClienteAGlobalVigente para AsignacionCartera.
            migrationBuilder.Sql("""
                WITH duplicados AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "TenantId", "ProyectoId", "TrabajadorId"
                               ORDER BY "FechaAlta" DESC, "Id" DESC
                           ) AS orden
                    FROM "ProyectosTecnicos"
                    WHERE "FechaBaja" IS NULL
                )
                UPDATE "ProyectosTecnicos" pt
                SET "FechaBaja" = pt."FechaAlta"
                FROM duplicados d
                WHERE pt."Id" = d."Id" AND d.orden > 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_ProyectosTecnicos_TenantId_ProyectoId_TrabajadorId_FechaAlta",
                table: "ProyectosTecnicos");

            migrationBuilder.CreateIndex(
                name: "IX_ProyectosTecnicos_TenantId_ProyectoId_TrabajadorId_Activo",
                table: "ProyectosTecnicos",
                columns: new[] { "TenantId", "ProyectoId", "TrabajadorId" },
                unique: true,
                filter: "\"FechaBaja\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProyectosTecnicos_TenantId_ProyectoId_TrabajadorId_Activo",
                table: "ProyectosTecnicos");

            migrationBuilder.CreateIndex(
                name: "IX_ProyectosTecnicos_TenantId_ProyectoId_TrabajadorId_FechaAlta",
                table: "ProyectosTecnicos",
                columns: new[] { "TenantId", "ProyectoId", "TrabajadorId", "FechaAlta" },
                unique: true);
        }
    }
}
