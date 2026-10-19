using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ContinuaCorreoTrasCierre : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversaciones_TenantId_ConexionIntegracionId_HiloExternoId",
                table: "Conversaciones");

            migrationBuilder.AddColumn<Guid>(
                name: "ConversacionAnteriorId",
                table: "Conversaciones",
                type: "uuid",
                nullable: true);

            // Igual que las tablas hijas: el enlace nunca puede apuntar a otro Tenant propietario.
            migrationBuilder.Sql("""
                ALTER TABLE public."Conversaciones"
                    ADD CONSTRAINT "FK_Conversaciones_Anterior_Tenant" FOREIGN KEY ("ConversacionAnteriorId", "TenantId")
                    REFERENCES public."Conversaciones" ("Id", "TenantId") ON DELETE RESTRICT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Conversaciones_TenantId_ConexionIntegracionId_HiloExternoId",
                table: "Conversaciones",
                columns: new[] { "TenantId", "ConexionIntegracionId", "HiloExternoId" },
                unique: true,
                filter: "\"Estado\" <> 3 AND NOT \"EstaEliminado\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE public."Conversaciones" DROP CONSTRAINT "FK_Conversaciones_Anterior_Tenant";""");
            migrationBuilder.DropIndex(
                name: "IX_Conversaciones_TenantId_ConexionIntegracionId_HiloExternoId",
                table: "Conversaciones");

            migrationBuilder.DropColumn(
                name: "ConversacionAnteriorId",
                table: "Conversaciones");

            migrationBuilder.CreateIndex(
                name: "IX_Conversaciones_TenantId_ConexionIntegracionId_HiloExternoId",
                table: "Conversaciones",
                columns: new[] { "TenantId", "ConexionIntegracionId", "HiloExternoId" },
                unique: true);
        }
    }
}
