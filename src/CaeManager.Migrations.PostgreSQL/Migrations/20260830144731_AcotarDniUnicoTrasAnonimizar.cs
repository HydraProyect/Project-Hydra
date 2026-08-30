using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AcotarDniUnicoTrasAnonimizar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_TenantId_Dni",
                table: "Trabajadores");

            migrationBuilder.AlterColumn<string>(
                name: "Dni",
                table: "Trabajadores",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            // Auditoría Módulo 5, hallazgo crítico 9/9: Trabajador.Anonimizar
            // vaciaba el Dni a '' en vez de a null. El índice único anterior
            // (sin filtro, sobre columna NOT NULL) ya impedía que dos
            // trabajadores del mismo tenant llegaran los dos a tener Dni='',
            // así que a lo sumo hay una fila heredada por tenant que
            // convertir — pero se hace por AnonimizadoEnUtc, no por
            // "Dni = ''", para no depender de que ningún otro dato legítimo
            // coincidiera alguna vez con la cadena vacía.
            migrationBuilder.Sql("""
                UPDATE "Trabajadores"
                SET "Dni" = NULL
                WHERE "AnonimizadoEnUtc" IS NOT NULL AND "Dni" = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_TenantId_Dni",
                table: "Trabajadores",
                columns: new[] { "TenantId", "Dni" },
                unique: true,
                filter: "\"Dni\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_TenantId_Dni",
                table: "Trabajadores");

            migrationBuilder.Sql("""
                UPDATE "Trabajadores"
                SET "Dni" = ''
                WHERE "Dni" IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Dni",
                table: "Trabajadores",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_TenantId_Dni",
                table: "Trabajadores",
                columns: new[] { "TenantId", "Dni" },
                unique: true);
        }
    }
}
