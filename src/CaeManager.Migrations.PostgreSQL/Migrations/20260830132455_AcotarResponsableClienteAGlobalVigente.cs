using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AcotarResponsableClienteAGlobalVigente : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Auditoría Módulo 5, hallazgo crítico 3/9: el índice único anterior
            // era POR OPERACIÓN, así que una cartera interna y una externa
            // vigentes sobre el mismo cliente podían convivir — dos reasignaciones
            // concurrentes hacia operaciones distintas dejaban dos operadores con
            // acceso simultáneo. Antes de acotar el índice a GLOBAL por
            // propietario-cliente hay que cerrar cualquier duplicado heredado, o
            // la creación del índice fallaría contra filas ya existentes. Se
            // conserva la más reciente (VigenciaDesde, desempate por Id) y se
            // cierran las demás como Transferida — el mismo motivo que usa
            // ReasignarCarteraClienteAsync para "otra ya responde de este ámbito".
            migrationBuilder.Sql("""
                WITH duplicados AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "PropietarioTenantId", "AmbitoRelacionClienteId"
                               ORDER BY "VigenciaDesde" DESC, "Id" DESC
                           ) AS orden
                    FROM "AsignacionesCartera"
                    WHERE "Estado" = 'Vigente'
                      AND "AmbitoRelacionClienteId" IS NOT NULL
                      AND "AmbitoCentroId" IS NULL
                      AND "AmbitoTrabajadorId" IS NULL
                      AND "AmbitoProyectoId" IS NULL
                )
                UPDATE "AsignacionesCartera" c
                SET "Estado" = 'Cerrada',
                    "MotivoCierre" = 'Transferida',
                    "VigenciaHasta" = NOW()
                FROM duplicados d
                WHERE c."Id" = d."Id" AND d.orden > 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_AsignacionesCartera_PropietarioTenantId_AmbitoRelacionClien~",
                table: "AsignacionesCartera");

            migrationBuilder.DropIndex(
                name: "IX_AsignacionesCartera_ResponsableRelacionVigente",
                table: "AsignacionesCartera");

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_ResponsableRelacionVigente",
                table: "AsignacionesCartera",
                columns: new[] { "PropietarioTenantId", "AmbitoRelacionClienteId" },
                unique: true,
                filter: "\"Estado\" = 'Vigente' AND \"AmbitoRelacionClienteId\" IS NOT NULL AND \"AmbitoCentroId\" IS NULL AND \"AmbitoTrabajadorId\" IS NULL AND \"AmbitoProyectoId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AsignacionesCartera_ResponsableRelacionVigente",
                table: "AsignacionesCartera");

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_PropietarioTenantId_AmbitoRelacionClien~",
                table: "AsignacionesCartera",
                columns: new[] { "PropietarioTenantId", "AmbitoRelacionClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_ResponsableRelacionVigente",
                table: "AsignacionesCartera",
                columns: new[] { "AsignacionOperacionId", "AmbitoRelacionClienteId" },
                unique: true,
                filter: "\"Estado\" = 'Vigente' AND \"AmbitoRelacionClienteId\" IS NOT NULL AND \"AmbitoCentroId\" IS NULL AND \"AmbitoTrabajadorId\" IS NULL AND \"AmbitoProyectoId\" IS NULL");
        }
    }
}
