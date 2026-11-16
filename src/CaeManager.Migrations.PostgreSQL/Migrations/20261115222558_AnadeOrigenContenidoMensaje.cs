using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeOrigenContenidoMensaje : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OrigenContenido",
                table: "Mensajes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RespuestaSugeridaId",
                table: "Mensajes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Mensajes_TenantId_RespuestaSugeridaId",
                table: "Mensajes",
                columns: new[] { "TenantId", "RespuestaSugeridaId" },
                filter: "\"RespuestaSugeridaId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Mensajes_TenantId_RespuestaSugeridaId",
                table: "Mensajes");

            migrationBuilder.DropColumn(
                name: "OrigenContenido",
                table: "Mensajes");

            migrationBuilder.DropColumn(
                name: "RespuestaSugeridaId",
                table: "Mensajes");
        }
    }
}
