using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarEnlaceDocumentoYDecisionHumanaAuditoriaIa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DecisionHumana",
                table: "AuditoriasExtraccionIa",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DocumentoId",
                table: "AuditoriasExtraccionIa",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaDecisionUtc",
                table: "AuditoriasExtraccionIa",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UsuarioDecisionId",
                table: "AuditoriasExtraccionIa",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditoriasExtraccionIa_DocumentoId",
                table: "AuditoriasExtraccionIa",
                column: "DocumentoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditoriasExtraccionIa_DocumentoId",
                table: "AuditoriasExtraccionIa");

            migrationBuilder.DropColumn(
                name: "DecisionHumana",
                table: "AuditoriasExtraccionIa");

            migrationBuilder.DropColumn(
                name: "DocumentoId",
                table: "AuditoriasExtraccionIa");

            migrationBuilder.DropColumn(
                name: "FechaDecisionUtc",
                table: "AuditoriasExtraccionIa");

            migrationBuilder.DropColumn(
                name: "UsuarioDecisionId",
                table: "AuditoriasExtraccionIa");
        }
    }
}
