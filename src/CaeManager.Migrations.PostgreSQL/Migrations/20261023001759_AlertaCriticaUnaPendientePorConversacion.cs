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
