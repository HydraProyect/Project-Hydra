using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RevocarAsignacionesOperadorDelegadoConRolDePropiedad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AsignacionesOperadorDelegado_DelegacionTenantId_UsuarioId",
                table: "AsignacionesOperadorDelegado");

            migrationBuilder.AddColumn<string>(
                name: "MotivoRevocacion",
                table: "AsignacionesOperadorDelegado",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevocadaEnUtc",
                table: "AsignacionesOperadorDelegado",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperadorDelegado_DelegacionTenantId_UsuarioId",
                table: "AsignacionesOperadorDelegado",
                columns: new[] { "DelegacionTenantId", "UsuarioId" },
                unique: true,
                filter: "\"RevocadaEnUtc\" IS NULL");

            // P8 (decisión del propietario 2026-09-23): Administrador y
            // Dirección CAE son roles del plano de Propiedad y ninguna
            // delegación ni Asignación de Cartera externa los concede. Las
            // filas heredadas que aún los llevan se revocan aquí, sin borrar
            // nada: la asignación de Operador Delegado queda marcada como
            // revocada y la cartera externa se cierra con motivo Revocada,
            // conservando las dos su historial.
            migrationBuilder.Sql($"""
                UPDATE "AsignacionesOperadorDelegado"
                SET "RevocadaEnUtc" = now(),
                    "MotivoRevocacion" = '{MotivoRevocacion}'
                WHERE "Rol" IN ('Administrador', 'DireccionCae')
                  AND "RevocadaEnUtc" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "AsignacionesCartera" AS c
                SET "Estado" = 'Cerrada',
                    "MotivoCierre" = 'Revocada',
                    "VigenciaHasta" = CASE
                        WHEN c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > now()
                            THEN GREATEST(now(), c."VigenciaDesde")
                        ELSE c."VigenciaHasta"
                    END,
                    "Version" = gen_random_uuid()
                FROM "AsignacionesOperacion" AS o
                WHERE o."Id" = c."AsignacionOperacionId"
                  AND o."OperadorTenantId" <> o."PropietarioTenantId"
                  AND c."Rol" IN ('Administrador', 'DireccionCae')
                  AND c."Estado" <> 'Cerrada';
                """);
        }

        /// <summary>
        /// Texto fijo que queda en cada fila revocada por esta migración.
        /// </summary>
        public const string MotivoRevocacion =
            "Rol de Propiedad no delegable (decisión del propietario 2026-09-23, P8)";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Bajar esta migración borraría la marca de revocación y las filas
            // volverían a conceder su rol: sería una reactivación silenciosa.
            // Se niega mientras quede alguna revocada; revertir exige decidir
            // antes qué hacer con ellas.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "AsignacionesOperadorDelegado" WHERE "RevocadaEnUtc" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Hay asignaciones de Operador Delegado revocadas: bajar esta migración las reactivaría.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_AsignacionesOperadorDelegado_DelegacionTenantId_UsuarioId",
                table: "AsignacionesOperadorDelegado");

            migrationBuilder.DropColumn(
                name: "MotivoRevocacion",
                table: "AsignacionesOperadorDelegado");

            migrationBuilder.DropColumn(
                name: "RevocadaEnUtc",
                table: "AsignacionesOperadorDelegado");

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesOperadorDelegado_DelegacionTenantId_UsuarioId",
                table: "AsignacionesOperadorDelegado",
                columns: new[] { "DelegacionTenantId", "UsuarioId" },
                unique: true);
        }
    }
}
