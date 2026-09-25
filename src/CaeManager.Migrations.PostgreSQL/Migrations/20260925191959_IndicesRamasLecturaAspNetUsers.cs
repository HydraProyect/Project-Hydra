using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class IndicesRamasLecturaAspNetUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RegistrosActividadSoporte_TenantId_UsuarioSoporteId",
                table: "RegistrosActividadSoporte",
                columns: new[] { "TenantId", "UsuarioSoporteId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAccesoDocumentoSensible_TenantId_ActorRealUsuarioId",
                table: "RegistrosAccesoDocumentoSensible",
                columns: new[] { "TenantId", "ActorRealUsuarioId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAccesoDocumentoSensible_TenantId_UsuarioId",
                table: "RegistrosAccesoDocumentoSensible",
                columns: new[] { "TenantId", "UsuarioId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RegistrosActividadSoporte_TenantId_UsuarioSoporteId",
                table: "RegistrosActividadSoporte");

            migrationBuilder.DropIndex(
                name: "IX_RegistrosAccesoDocumentoSensible_TenantId_ActorRealUsuarioId",
                table: "RegistrosAccesoDocumentoSensible");

            migrationBuilder.DropIndex(
                name: "IX_RegistrosAccesoDocumentoSensible_TenantId_UsuarioId",
                table: "RegistrosAccesoDocumentoSensible");
        }
    }
}
