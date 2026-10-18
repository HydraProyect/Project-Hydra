using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class PrioridadYSlaConversacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EventoOrigenId",
                table: "NotificacionesUsuario",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EsAutomatico",
                table: "Mensajes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "AvisoCriticaPendienteId",
                table: "Conversaciones",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Prioridad",
                table: "Conversaciones",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_NotificacionesUsuario_TenantId_UsuarioDestinatarioId_Evento~",
                table: "NotificacionesUsuario",
                columns: new[] { "TenantId", "UsuarioDestinatarioId", "EventoOrigenId" },
                unique: true,
                filter: "\"EventoOrigenId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificacionesUsuario_TenantId_UsuarioDestinatarioId_Evento~",
                table: "NotificacionesUsuario");

            migrationBuilder.DropColumn(
                name: "EventoOrigenId",
                table: "NotificacionesUsuario");

            migrationBuilder.DropColumn(
                name: "EsAutomatico",
                table: "Mensajes");

            migrationBuilder.DropColumn(
                name: "AvisoCriticaPendienteId",
                table: "Conversaciones");

            migrationBuilder.DropColumn(
                name: "Prioridad",
                table: "Conversaciones");
        }
    }
}
