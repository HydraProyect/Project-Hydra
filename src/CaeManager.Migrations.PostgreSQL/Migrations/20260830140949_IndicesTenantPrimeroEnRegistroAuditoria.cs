using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class IndicesTenantPrimeroEnRegistroAuditoria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RegistrosAuditoria_ActorRealUsuarioId",
                table: "RegistrosAuditoria");

            migrationBuilder.DropIndex(
                name: "IX_RegistrosAuditoria_EntidadTipo_EntidadId",
                table: "RegistrosAuditoria");

            migrationBuilder.DropIndex(
                name: "IX_RegistrosAuditoria_FechaUtc",
                table: "RegistrosAuditoria");

            migrationBuilder.DropIndex(
                name: "IX_RegistrosAuditoria_ViaAccesoId",
                table: "RegistrosAuditoria");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_TenantId_ActorRealUsuarioId_FechaUtc",
                table: "RegistrosAuditoria",
                columns: new[] { "TenantId", "ActorRealUsuarioId", "FechaUtc" },
                filter: "\"ActorRealUsuarioId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_TenantId_EntidadTipo_EntidadId_FechaUtc",
                table: "RegistrosAuditoria",
                columns: new[] { "TenantId", "EntidadTipo", "EntidadId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_TenantId_FechaUtc",
                table: "RegistrosAuditoria",
                columns: new[] { "TenantId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_TenantId_UsuarioId_FechaUtc",
                table: "RegistrosAuditoria",
                columns: new[] { "TenantId", "UsuarioId", "FechaUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_TenantId_ViaAccesoId",
                table: "RegistrosAuditoria",
                columns: new[] { "TenantId", "ViaAccesoId" },
                filter: "\"ViaAccesoId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RegistrosAuditoria_TenantId_ActorRealUsuarioId_FechaUtc",
                table: "RegistrosAuditoria");

            migrationBuilder.DropIndex(
                name: "IX_RegistrosAuditoria_TenantId_EntidadTipo_EntidadId_FechaUtc",
                table: "RegistrosAuditoria");

            migrationBuilder.DropIndex(
                name: "IX_RegistrosAuditoria_TenantId_FechaUtc",
                table: "RegistrosAuditoria");

            migrationBuilder.DropIndex(
                name: "IX_RegistrosAuditoria_TenantId_UsuarioId_FechaUtc",
                table: "RegistrosAuditoria");

            migrationBuilder.DropIndex(
                name: "IX_RegistrosAuditoria_TenantId_ViaAccesoId",
                table: "RegistrosAuditoria");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_ActorRealUsuarioId",
                table: "RegistrosAuditoria",
                column: "ActorRealUsuarioId",
                filter: "\"ActorRealUsuarioId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_EntidadTipo_EntidadId",
                table: "RegistrosAuditoria",
                columns: new[] { "EntidadTipo", "EntidadId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_FechaUtc",
                table: "RegistrosAuditoria",
                column: "FechaUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_ViaAccesoId",
                table: "RegistrosAuditoria",
                column: "ViaAccesoId",
                filter: "\"ViaAccesoId\" IS NOT NULL");
        }
    }
}
