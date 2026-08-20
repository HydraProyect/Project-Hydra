using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AuditoriaConIdentidadDual : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActorRealUsuarioId",
                table: "RegistrosAuditoria",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ViaAcceso",
                table: "RegistrosAuditoria",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ViaAccesoId",
                table: "RegistrosAuditoria",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_ActorRealUsuarioId",
                table: "RegistrosAuditoria",
                column: "ActorRealUsuarioId",
                filter: "\"ActorRealUsuarioId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_ViaAccesoId",
                table: "RegistrosAuditoria",
                column: "ViaAccesoId",
                filter: "\"ViaAccesoId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RegistrosAuditoria_ActorRealUsuarioId",
                table: "RegistrosAuditoria");

            migrationBuilder.DropIndex(
                name: "IX_RegistrosAuditoria_ViaAccesoId",
                table: "RegistrosAuditoria");

            migrationBuilder.DropColumn(
                name: "ActorRealUsuarioId",
                table: "RegistrosAuditoria");

            migrationBuilder.DropColumn(
                name: "ViaAcceso",
                table: "RegistrosAuditoria");

            migrationBuilder.DropColumn(
                name: "ViaAccesoId",
                table: "RegistrosAuditoria");
        }
    }
}
