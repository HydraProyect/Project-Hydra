using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarClasificacionRuidoMensaje : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClasificacionesRuidoMensaje",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MensajeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EsNotificacionAutomatica = table.Column<bool>(type: "boolean", nullable: false),
                    ProveedorPlataformaCaeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Motivo = table.Column<int>(type: "integer", nullable: false),
                    ConfirmadaManualmente = table.Column<bool>(type: "boolean", nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClasificacionesRuidoMensaje", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClasificacionesRuidoMensaje_MensajeId",
                table: "ClasificacionesRuidoMensaje",
                column: "MensajeId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClasificacionesRuidoMensaje");
        }
    }
}
