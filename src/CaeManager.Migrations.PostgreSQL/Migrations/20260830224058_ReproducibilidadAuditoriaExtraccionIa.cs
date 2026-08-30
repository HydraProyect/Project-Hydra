using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ReproducibilidadAuditoriaExtraccionIa : Migration
    {
        /// <summary>
        /// Las filas ya existentes quedan con VersionPipeline = "" y los tres
        /// campos nuevos en null: no consta con qué versión de pipeline ni con
        /// qué modelo se procesaron (se escribieron antes de que este cambio
        /// existiera), así que inventar un valor sería afirmar algo que no
        /// sabemos. Mismo criterio que la migración
        /// ClaveCompletaEnExtraccionIaCache para ExtraccionIaCache.TipoEsperado.
        /// </summary>
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModeloExacto",
                table: "AuditoriasExtraccionIa",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProveedoresInvocados",
                table: "AuditoriasExtraccionIa",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                table: "AuditoriasExtraccionIa",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VersionPipeline",
                table: "AuditoriasExtraccionIa",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModeloExacto",
                table: "AuditoriasExtraccionIa");

            migrationBuilder.DropColumn(
                name: "ProveedoresInvocados",
                table: "AuditoriasExtraccionIa");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "AuditoriasExtraccionIa");

            migrationBuilder.DropColumn(
                name: "VersionPipeline",
                table: "AuditoriasExtraccionIa");
        }
    }
}
