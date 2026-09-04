using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarRegistroAccesoDocumentoSensible : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegistrosAccesoDocumentoSensible",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sensibilidad = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TipoAcceso = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorRealUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    ViaAcceso = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ViaAccesoId = table.Column<Guid>(type: "uuid", nullable: true),
                    OcurridoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrosAccesoDocumentoSensible", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAccesoDocumentoSensible_DocumentoId_OcurridoEnUtc",
                table: "RegistrosAccesoDocumentoSensible",
                columns: new[] { "DocumentoId", "OcurridoEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAccesoDocumentoSensible_TenantId_OcurridoEnUtc",
                table: "RegistrosAccesoDocumentoSensible",
                columns: new[] { "TenantId", "OcurridoEnUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistrosAccesoDocumentoSensible");
        }
    }
}
