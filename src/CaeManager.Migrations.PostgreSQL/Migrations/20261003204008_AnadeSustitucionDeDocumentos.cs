using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Documento efectivo, PR 1 (diseño 2026-10-03, decisiones D5 y D8): <b>migración aditiva</b> que da a
    /// <c>Documentos</c> la sustitución explícita, para que un documento renovado pase al historial en vez de
    /// pisarse. Añade tres columnas nulas (<c>SustituidoPorDocumentoId</c>, <c>SustituidoEnUtc</c>,
    /// <c>MotivoSustitucion</c>), dos CHECK (<c>CK_Documentos_SustitucionCoherente</c>: las tres columnas juntas o
    /// ninguna y motivo entre 1 y 3; <c>CK_Documentos_NoSeSustituyeASiMismo</c>), el índice de la FK y la FK
    /// compuesta <c>(TenantId, SustituidoPorDocumentoId)</c> hacia <c>(TenantId, Id)</c> de la propia tabla (el
    /// sustituto es del mismo Tenant propietario). No transforma datos —toda fila existente queda con las tres
    /// columnas nulas, es decir, operativa—, no toca RLS (la tabla ya tiene su política por <c>TenantId</c> y las
    /// columnas nuevas viven en ella) y ningún lector la consulta todavía. <c>Down</c> deshace las seis cosas.
    /// </summary>
    public partial class AnadeSustitucionDeDocumentos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MotivoSustitucion",
                table: "Documentos",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SustituidoEnUtc",
                table: "Documentos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SustituidoPorDocumentoId",
                table: "Documentos",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TenantId_SustituidoPorDocumentoId",
                table: "Documentos",
                columns: new[] { "TenantId", "SustituidoPorDocumentoId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Documentos_NoSeSustituyeASiMismo",
                table: "Documentos",
                sql: "\"SustituidoPorDocumentoId\" IS DISTINCT FROM \"Id\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Documentos_SustitucionCoherente",
                table: "Documentos",
                sql: "num_nonnulls(\"SustituidoPorDocumentoId\", \"SustituidoEnUtc\", \"MotivoSustitucion\") IN (0, 3) AND (\"MotivoSustitucion\" IS NULL OR \"MotivoSustitucion\" IN (1, 2, 3))");

            migrationBuilder.AddForeignKey(
                name: "FK_Documentos_Documentos_TenantId_SustituidoPorDocumentoId",
                table: "Documentos",
                columns: new[] { "TenantId", "SustituidoPorDocumentoId" },
                principalTable: "Documentos",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Documentos_Documentos_TenantId_SustituidoPorDocumentoId",
                table: "Documentos");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_TenantId_SustituidoPorDocumentoId",
                table: "Documentos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Documentos_NoSeSustituyeASiMismo",
                table: "Documentos");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Documentos_SustitucionCoherente",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "MotivoSustitucion",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "SustituidoEnUtc",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "SustituidoPorDocumentoId",
                table: "Documentos");
        }
    }
}
