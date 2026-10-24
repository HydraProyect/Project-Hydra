using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AlertaCriticaUnaPendientePorConversacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AlertasCriticasConversacion_TenantId_ConversacionId",
                table: "AlertasCriticasConversacion");

            migrationBuilder.AddColumn<Guid>(
                name: "IncidenciaId",
                table: "AlertasCriticasConversacion",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertasCriticasConversacion_TenantId_ConversacionId_Pendiente",
                table: "AlertasCriticasConversacion",
                columns: new[] { "TenantId", "ConversacionId" },
                unique: true,
                filter: "\"Estado\" = 0");
        }

        /// <inheritdoc />
        // Volver atrás exige que cada conversación tenga como mucho una alerta: con una resuelta y otra posterior en el mismo hilo,
        // el índice único completo no se puede recrear y la reversión falla sin borrar datos.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AlertasCriticasConversacion_TenantId_ConversacionId_Pendiente",
                table: "AlertasCriticasConversacion");

            migrationBuilder.DropColumn(
                name: "IncidenciaId",
                table: "AlertasCriticasConversacion");

            migrationBuilder.CreateIndex(
                name: "IX_AlertasCriticasConversacion_TenantId_ConversacionId",
                table: "AlertasCriticasConversacion",
                columns: new[] { "TenantId", "ConversacionId" },
                unique: true);
        }
    }
}
