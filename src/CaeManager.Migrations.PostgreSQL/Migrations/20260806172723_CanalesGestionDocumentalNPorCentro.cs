using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class CanalesGestionDocumentalNPorCentro : Migration
    {
        /// <inheritdoc />
        // Lote 0-E (PLAN-EJECUCION-UX.md § 0.6): CanalGestionDocumental pasa de
        // 1:1 a N por Centro, y de EntidadConTenant a EntidadBase (Version +
        // soft delete, porque ahora se edita desde la UI).
        //
        // Los defaultValue/defaultValueSql de las columnas nuevas están puestos
        // a mano sobre lo que generó `dotnet ef migrations add` (que rellenaba
        // fecha 0001-01-01, Guid vacío y cadena vacía): en la práctica la tabla
        // no tiene filas en ningún entorno — nunca existió un escritor, ni
        // Command ni seeder, solo la Query de lectura (ROADMAP.md, fase "Canal
        // de gestión documental") — pero un Version a Guid.Empty desactivaría
        // en silencio la comprobación de concurrencia de esas filas, y "" no es
        // una etiqueta de propósito válida en el dominio.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CanalesGestionDocumental_TenantId_CentroId",
                table: "CanalesGestionDocumental");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreadoEnUtc",
                table: "CanalesGestionDocumental",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "NOW()");

            migrationBuilder.AddColumn<DateTime>(
                name: "EliminadoEnUtc",
                table: "CanalesGestionDocumental",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EliminadoPorUsuarioId",
                table: "CanalesGestionDocumental",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EsPrincipal",
                table: "CanalesGestionDocumental",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EstaEliminado",
                table: "CanalesGestionDocumental",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EtiquetaProposito",
                table: "CanalesGestionDocumental",
                type: "character varying(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "Gestión general");

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "CanalesGestionDocumental",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            // Los canales que ya existieran pasan a ser el principal de su
            // Centro: eran el único acceso posible cuando la relación era 1:1.
            migrationBuilder.Sql(
                """
                UPDATE "CanalesGestionDocumental" SET "EsPrincipal" = true;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CanalesGestionDocumental_TenantId_CentroId",
                table: "CanalesGestionDocumental",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_CanalesGestionDocumental_TenantId_CentroId_Principal",
                table: "CanalesGestionDocumental",
                columns: new[] { "TenantId", "CentroId" },
                unique: true,
                filter: "\"EsPrincipal\" AND NOT \"EstaEliminado\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CanalesGestionDocumental_TenantId_CentroId",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropIndex(
                name: "IX_CanalesGestionDocumental_TenantId_CentroId_Principal",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropColumn(
                name: "CreadoEnUtc",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropColumn(
                name: "EliminadoEnUtc",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropColumn(
                name: "EliminadoPorUsuarioId",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropColumn(
                name: "EsPrincipal",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropColumn(
                name: "EstaEliminado",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropColumn(
                name: "EtiquetaProposito",
                table: "CanalesGestionDocumental");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "CanalesGestionDocumental");

            migrationBuilder.CreateIndex(
                name: "IX_CanalesGestionDocumental_TenantId_CentroId",
                table: "CanalesGestionDocumental",
                columns: new[] { "TenantId", "CentroId" },
                unique: true);
        }
    }
}
